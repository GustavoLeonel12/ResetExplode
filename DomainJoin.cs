using System.DirectoryServices;
using System.Management;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace ResetExplode;

/// <summary>
/// Ingresso no domínio (AD) com nome padronizado: SEGMENTO + TIPO + 3 dígitos (ex.: LABARPC001).
/// Consulta o AD por LDAP com a conta informada, escolhe o próximo número livre, renomeia e
/// ingressa num passo só (NetJoinDomain com JOIN_WITH_NEW_NAME). A senha nunca é salva nem registrada.
/// </summary>
internal static class DomainJoin
{
    public const string DefaultDomain = "luft.com.br";

    // Win32_SystemEnclosure.ChassisTypes que são portáteis.
    private static readonly HashSet<int> LaptopChassis = new() { 8, 9, 10, 11, 12, 14, 18, 21, 30, 31, 32 };

    /// <summary>"NB" para notebook, "PC" para desktop.</summary>
    public static string DetectType()
    {
        try
        {
            using var s = new ManagementObjectSearcher("SELECT ChassisTypes FROM Win32_SystemEnclosure");
            foreach (ManagementObject o in s.Get())
                if (o["ChassisTypes"] is ushort[] types && types.Any(t => LaptopChassis.Contains(t)))
                    return "NB";
        }
        catch { }
        try
        {
            using var b = new ManagementObjectSearcher("SELECT Name FROM Win32_Battery");
            if (b.Get().Count > 0) return "NB";
        }
        catch { }
        return "PC";
    }

    public static bool IsHomeEdition()
    {
        try
        {
            using var s = new ManagementObjectSearcher("SELECT Caption FROM Win32_OperatingSystem");
            foreach (ManagementObject o in s.Get())
                if ((o["Caption"] as string ?? "").Contains("Home", StringComparison.OrdinalIgnoreCase)) return true;
        }
        catch { }
        return false;
    }

    /// <summary>Domínio atual da máquina, ou null se estiver em grupo de trabalho.</summary>
    public static string? CurrentDomain()
    {
        try
        {
            using var s = new ManagementObjectSearcher("SELECT PartOfDomain, Domain FROM Win32_ComputerSystem");
            foreach (ManagementObject o in s.Get())
                if (o["PartOfDomain"] is bool b && b) return o["Domain"] as string;
        }
        catch { }
        return null;
    }

    /// <summary>Usuário sem domínio vira UPN (usuario@dominio).</summary>
    public static string QualifyUser(string user, string domain)
    {
        user = user.Trim();
        return user.Contains('@') || user.Contains('\\') ? user : $"{user}@{domain}";
    }

    public static bool IsValidName(string name) =>
        Regex.IsMatch(name, "^[A-Za-z0-9-]{1,15}$") && !Regex.IsMatch(name, "^[0-9]+$");

    private static DirectoryEntry Root(string domain, string user, string password) =>
        new($"LDAP://{domain}", QualifyUser(user, domain), password, AuthenticationTypes.Secure | AuthenticationTypes.Sealing);

    /// <summary>Todos os nomes de computador no AD que começam com o prefixo (em maiúsculas).</summary>
    public static HashSet<string> ExistingNames(string domain, string prefix, string user, string password)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var root = Root(domain, user, password);
        using var ds = new DirectorySearcher(root)
        {
            Filter = $"(&(objectCategory=computer)(|(cn={Escape(prefix)}*)(sAMAccountName={Escape(prefix)}*)))",
            PageSize = 1000,
            SearchScope = SearchScope.Subtree,
        };
        ds.PropertiesToLoad.Add("cn");
        ds.PropertiesToLoad.Add("sAMAccountName");
        using var results = ds.FindAll();
        foreach (SearchResult r in results)
        {
            if (r.Properties["cn"].Count > 0) names.Add(r.Properties["cn"][0]!.ToString()!);
            if (r.Properties["sAMAccountName"].Count > 0) names.Add(r.Properties["sAMAccountName"][0]!.ToString()!.TrimEnd('$'));
        }
        return names;
    }

    /// <summary>Próximo nome livre: maior número existente + 1, com 3 dígitos (LABARPC001).</summary>
    public static (string Name, int Count) NextName(string domain, string prefix, string user, string password)
    {
        var existing = ExistingNames(domain, prefix, user, password);
        var rx = new Regex("^" + Regex.Escape(prefix) + "([0-9]+)$", RegexOptions.IgnoreCase);
        int max = 0;
        foreach (var n in existing)
        {
            var m = rx.Match(n);
            if (m.Success && int.TryParse(m.Groups[1].Value, out int v) && v > max) max = v;
        }
        string next;
        int i = max + 1;
        do { next = prefix + i.ToString("D3"); i++; } while (existing.Contains(next));
        return (next.ToUpperInvariant(), existing.Count);
    }

    /// <summary>true se já existe um computador com esse nome no AD.</summary>
    public static bool NameExists(string domain, string name, string user, string password)
    {
        using var root = Root(domain, user, password);
        using var ds = new DirectorySearcher(root)
        {
            Filter = $"(&(objectCategory=computer)(|(cn={Escape(name)})(sAMAccountName={Escape(name)}$)))",
            SearchScope = SearchScope.Subtree,
        };
        ds.PropertiesToLoad.Add("cn");
        return ds.FindOne() != null;
    }

    private static string Escape(string s)
    {
        var sb = new System.Text.StringBuilder();
        foreach (char c in s)
            sb.Append(c switch { '\\' => @"\5c", '*' => @"\2a", '(' => @"\28", ')' => @"\29", '\0' => @"\00", _ => c.ToString() });
        return sb.ToString();
    }

    // ===== Renomear + ingressar =====

    private const int ComputerNamePhysicalDnsHostname = 5;
    private const uint NETSETUP_JOIN_DOMAIN = 0x1, NETSETUP_ACCT_CREATE = 0x2, NETSETUP_JOIN_WITH_NEW_NAME = 0x400;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetComputerNameEx(int nameType, string lpBuffer);

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetJoinDomain(string? lpServer, string lpDomain, string? lpMachineAccountOU,
        string? lpAccount, string? lpPassword, uint fJoinOptions);

    /// <summary>
    /// Renomeia para <paramref name="newName"/> e ingressa no domínio, criando a conta do computador
    /// (na OU informada, ou na padrão). Se falhar, desfaz a troca de nome. Exige reiniciar depois.
    /// </summary>
    public static void RenameAndJoin(string domain, string newName, string? ou, string user, string password)
    {
        string oldName = Environment.MachineName;
        bool renamed = false;
        if (!newName.Equals(oldName, StringComparison.OrdinalIgnoreCase))
        {
            if (!SetComputerNameEx(ComputerNamePhysicalDnsHostname, newName))
                throw new InvalidOperationException($"Não foi possível renomear a máquina (erro {Marshal.GetLastWin32Error()}).");
            renamed = true;
        }

        int rc = NetJoinDomain(null, domain, string.IsNullOrWhiteSpace(ou) ? null : ou.Trim(),
            QualifyUser(user, domain), password,
            NETSETUP_JOIN_DOMAIN | NETSETUP_ACCT_CREATE | (renamed ? NETSETUP_JOIN_WITH_NEW_NAME : 0));
        if (rc != 0)
        {
            if (renamed) SetComputerNameEx(ComputerNamePhysicalDnsHostname, oldName);
            throw new InvalidOperationException(JoinError(rc));
        }
    }

    private static string JoinError(int rc) => rc switch
    {
        5 => "Acesso negado: a conta não tem permissão para ingressar máquinas no domínio (ou na OU escolhida).",
        53 or 1355 => "Domínio não encontrado: confira se o PC está na rede da empresa e se o DNS aponta para o AD.",
        1326 => "Usuário ou senha incorretos.",
        1909 => "A conta está bloqueada.",
        2224 => "Já existe uma conta de computador com esse nome no AD.",
        2691 => "A máquina já está ingressada no domínio.",
        8557 => "A conta atingiu o limite de máquinas que pode ingressar no domínio. Use uma conta de TI com permissão delegada.",
        _ => $"Falha ao ingressar no domínio (código {rc}: {new System.ComponentModel.Win32Exception(rc).Message})."
    };
}
