using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Win32;

namespace ResetExplode;

/// <summary>App do catálogo "Instalar apps" (pós-formatação), instalado pelo winget.</summary>
public sealed class InstallableApp : INotifyPropertyChanged
{
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public string WingetId { get; init; } = "";
    /// <summary>Começo do nome em "Aplicativos instalados" (Painel de Controle), para saber se já está instalado.</summary>
    public string DisplayPrefix { get; init; } = "";
    public bool PreSelect { get; init; } = true;
    /// <summary>Office: ID do produto na Ferramenta de Implantação do Office (ODT). Quando definido, instala pelo ODT em vez do winget.</summary>
    public string? OfficeProduct { get; init; }

    private bool _selected, _installed;
    private string _status = "";

    public bool Selected { get => _selected; set => Set(ref _selected, value); }
    public bool Installed
    {
        get => _installed;
        set { Set(ref _installed, value); OnChanged(nameof(CanSelect)); }
    }
    public bool CanSelect => !Installed;
    public string Status { get => _status; set => Set(ref _status, value); }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? p = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
    private void Set<T>(ref T field, T value, [CallerMemberName] string? p = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        OnChanged(p);
    }
}

/// <summary>
/// Catálogo de apps essenciais e instalação silenciosa pelo winget (Gerenciador de Pacotes do Windows), que baixa
/// sempre a versão oficial mais recente do site do fabricante e confere o hash do instalador.
/// </summary>
public static class AppInstaller
{
    public static List<InstallableApp> Catalog() => new()
    {
        new() { Name = "Google Chrome", Description = "Navegador do Google.", WingetId = "Google.Chrome", DisplayPrefix = "Google Chrome" },
        new() { Name = "Firefox", Description = "Navegador da Mozilla (versão em português).", WingetId = "Mozilla.Firefox.pt-BR", DisplayPrefix = "Mozilla Firefox" },
        new() { Name = "Adobe Acrobat Reader", Description = "Leitor de PDF gratuito da Adobe (só o leitor, sem outros apps da Adobe).", WingetId = "Adobe.Acrobat.Reader.64-bit", DisplayPrefix = "Adobe Acrobat" },
        new() { Name = "AnyDesk", Description = "Acesso remoto para dar ou receber suporte.", WingetId = "AnyDesk.AnyDesk", DisplayPrefix = "AnyDesk" },
        new() { Name = "TeamViewer", Description = "Acesso remoto e suporte a distância.", WingetId = "TeamViewer.TeamViewer", DisplayPrefix = "TeamViewer" },
        new() { Name = "WinRAR", Description = "Compactador (zip, rar, 7z). Abre e cria arquivos compactados; pago após o teste.", WingetId = "RARLab.WinRAR", DisplayPrefix = "WinRAR" },
        new() { Name = "Office Professional Plus 2024", Description = "Word, Excel, PowerPoint, Outlook, Access e OneNote. Instalador oficial da Microsoft. Para ativar sozinho, digite a chave do cliente no campo abaixo da lista.",
                OfficeProduct = "ProPlus2024Retail", DisplayPrefix = "Microsoft Office Professional Plus 2024", PreSelect = false },
        new() { Name = "Office Home & Business 2024", Description = "Word, Excel, PowerPoint e Outlook. Instalador oficial da Microsoft. Para ativar sozinho, digite a chave do cliente no campo abaixo da lista.",
                OfficeProduct = "HomeBusiness2024Retail", DisplayPrefix = "Microsoft Office Home and Business 2024", PreSelect = false },
        new() { Name = "Lightshot", Description = "Captura de tela rápida com a tecla Print Screen.", WingetId = "Skillbrains.Lightshot", DisplayPrefix = "Lightshot" },
    };

    /// <summary>
    /// winget.exe: o atalho em %LOCALAPPDATA%\Microsoft\WindowsApps do usuário atual ou, se não houver (ex.: o app foi
    /// aberto como administrador com outra conta), o que estiver no PATH.
    /// </summary>
    public static string? WingetPath => _wingetPath ??= FindWinget();
    private static string? _wingetPath;

    private static string? FindWinget()
    {
        string alias = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\WindowsApps\winget.exe");
        if (File.Exists(alias)) return alias;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try { string f = Path.Combine(dir.Trim(), "winget.exe"); if (File.Exists(f)) return f; } catch { }
        }
        return null;
    }

    public static bool WingetAvailable => WingetPath != null;

    /// <summary>Log da última instalação (saída completa do winget/Office), para entender uma falha. Nunca contém a chave do Office.</summary>
    public static string LogPath => Path.Combine(AppSettings.Folder, "instalar-apps.log");

    /// <summary>Começa um log novo a cada rodada de instalação.</summary>
    public static void ResetLog() { try { File.Delete(LogPath); } catch { } }

    private static void Log(string text)
    {
        try
        {
            Directory.CreateDirectory(AppSettings.Folder);
            File.AppendAllText(LogPath, $"[{DateTime.Now:dd/MM/yyyy HH:mm:ss}] {text.TrimEnd()}{Environment.NewLine}{Environment.NewLine}", Encoding.UTF8);
        }
        catch { }
    }

    /// <summary>Roda o winget e devolve (código de saída, saída completa).</summary>
    private static async Task<(int Exit, string Output)> RunWingetAsync(string args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(WingetPath ?? "winget.exe", args)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Não foi possível iniciar o winget.");
        var outTask = p.StandardOutput.ReadToEndAsync(ct);
        var errTask = p.StandardError.ReadToEndAsync(ct);
        try { await p.WaitForExitAsync(ct); }
        catch (OperationCanceledException) { try { p.Kill(true); } catch { } throw; }
        return (p.ExitCode, await outTask + await errTask);
    }

    private static bool _wingetReady;
    private static bool _supportsDisableInteractivity = true;

    /// <summary>
    /// Uma vez por sessão: lê a versão do winget (o --disable-interactivity só existe a partir da 1.4) e atualiza o
    /// catálogo "winget". Num PC recém-formatado o catálogo ainda não foi baixado e toda instalação falha sem isso.
    /// </summary>
    private static async Task PrepareWingetAsync(CancellationToken ct)
    {
        if (_wingetReady) return;
        try
        {
            var (_, ver) = await RunWingetAsync("--version", ct);
            Log($"winget {ver.Trim()} em {WingetPath}");
            var m = System.Text.RegularExpressions.Regex.Match(ver, @"v?(\d+)\.(\d+)");
            if (m.Success)
            {
                int major = int.Parse(m.Groups[1].Value), minor = int.Parse(m.Groups[2].Value);
                _supportsDisableInteractivity = major > 1 || (major == 1 && minor >= 4);
            }
            var (code, upd) = await RunWingetAsync("source update --name winget", ct);
            if (code != 0) Log($"source update: 0x{code:X8}\n{upd}");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Log($"Preparar winget: {ex.Message}"); }
        _wingetReady = true;
    }

    /// <summary>Nomes em "Aplicativos instalados" (máquina 64/32 bits e usuário atual).</summary>
    public static HashSet<string> InstalledNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        const string path = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";
        foreach (var (hive, view) in new[] { (RegistryHive.LocalMachine, RegistryView.Registry64), (RegistryHive.LocalMachine, RegistryView.Registry32),
                                             (RegistryHive.CurrentUser, RegistryView.Default) })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view).OpenSubKey(path);
                if (root == null) continue;
                foreach (var sub in root.GetSubKeyNames())
                {
                    using var k = root.OpenSubKey(sub);
                    if (k?.GetValue("DisplayName") is string n && !string.IsNullOrWhiteSpace(n)) names.Add(n.Trim());
                }
            }
            catch { }
        }
        return names;
    }

    public static void RefreshInstalled(IEnumerable<InstallableApp> apps)
    {
        var names = InstalledNames();
        foreach (var a in apps)
        {
            a.Installed = a.OfficeProduct != null
                ? OfficeInstalled(a.OfficeProduct)
                : names.Any(n => n.StartsWith(a.DisplayPrefix, StringComparison.OrdinalIgnoreCase));
            if (a.Installed) a.Selected = false;
        }
    }

    /// <summary>Instala um app em silêncio. Retorna (ok, mensagem curta).</summary>
    public static async Task<(bool Ok, string Message)> InstallAsync(InstallableApp app, CancellationToken ct, bool upgrade = false, string? officeKey = null)
    {
        if (app.OfficeProduct != null) return await InstallOfficeAsync(app.OfficeProduct, officeKey, ct);

        if (!WingetAvailable) return (false, "O winget não foi encontrado neste PC (veja o botão da Microsoft Store abaixo da lista).");
        await PrepareWingetAsync(ct);

        // --source winget: sem ele o winget também consulta a Microsoft Store, e quando a loja não responde
        // (comum em PC recém-formatado ou rodando como administrador) a instalação inteira falha.
        string args = $"{(upgrade ? "upgrade" : "install")} --id {app.WingetId} --exact --source winget --silent " +
                      "--accept-package-agreements --accept-source-agreements" + (_supportsDisableInteractivity ? " --disable-interactivity" : "");
        var (exit, output) = await RunWingetAsync(args, ct);
        Log($"{app.Name}: winget {args}\ncódigo 0x{exit:X8}\n{Clean(output)}");

        switch (exit)
        {
            case 0: return (true, "Instalado.");
            case unchecked((int)0x8A15002B): case unchecked((int)0x8A150061): return (true, "Já estava instalado.");
            case unchecked((int)0x8A150109): return (true, "Instalado. Reinicie o PC para terminar.");
        }
        // Alguns instaladores devolvem código de erro mesmo instalando: confere em "Aplicativos instalados".
        if (!upgrade && !string.IsNullOrEmpty(app.DisplayPrefix)
            && InstalledNames().Any(n => n.StartsWith(app.DisplayPrefix, StringComparison.OrdinalIgnoreCase)))
            return (true, "Instalado.");

        return exit switch
        {
            unchecked((int)0x8A150014) => (false, "Pacote não encontrado no winget."),
            unchecked((int)0x8A150008) or unchecked((int)0x8A150007) => (false, "Falha no download: confira a internet e tente de novo."),
            unchecked((int)0x8A15000F) or unchecked((int)0x8A150044) => (false, "O catálogo do winget não respondeu. Confira a internet e tente de novo."),
            unchecked((int)0x8A150101) => (false, "O programa está aberto. Feche-o e tente de novo."),
            _ => (false, $"Falhou (código 0x{exit:X8}). {LastLine(output)}".Trim() + " Detalhes no log."),
        };
    }

    /// <summary>Saída sem as linhas de barra de progresso.</summary>
    private static string Clean(string output) => string.Join("\n",
        output.Split('\n', '\r').Select(l => l.Trim())
              .Where(l => l.Length > 3 && !l.All(c => "-\\|/█▒ ".Contains(c)) && !l.Contains("KB /") && !l.Contains("MB /")));

    // ---------------- Office (Ferramenta de Implantação do Office) ----------------

    /// <summary>setup.exe da Ferramenta de Implantação do Office, no CDN oficial da Microsoft.</summary>
    private const string OdtUrl = "https://officecdn.microsoft.com/pr/wsus/setup.exe";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) ResetExplode");
        return c;
    }

    /// <summary>Produtos Click-to-Run instalados (ex.: "ProPlus2024Retail,HomeBusiness2024Retail").</summary>
    public static bool OfficeInstalled(string productId)
    {
        try
        {
            using var k = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                                     .OpenSubKey(@"SOFTWARE\Microsoft\Office\ClickToRun\Configuration");
            return (k?.GetValue("ProductReleaseIds") as string ?? "")
                .Split(',', StringSplitOptions.TrimEntries).Contains(productId, StringComparer.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>
    /// Baixa o setup.exe oficial do ODT (confere a assinatura da Microsoft) e instala o produto em silêncio,
    /// 64 bits, no idioma do Windows. Sem chave, o Office fica instalado e pede a licença ao abrir. Com a chave do
    /// cliente, ela vai no PIDKEY e o próprio instalador ativa (AUTOACTIVATE); o XML com a chave é apagado no fim.
    /// </summary>
    private static async Task<(bool Ok, string Message)> InstallOfficeAsync(string productId, string? key, CancellationToken ct)
    {
        string dir = Path.Combine(Path.GetTempPath(), "ResetExplode", "office");
        Directory.CreateDirectory(dir);
        string setup = Path.Combine(dir, "setup.exe");
        try
        {
            using (var resp = await Http.GetAsync(OdtUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                resp.EnsureSuccessStatusCode();
                await using var fs = File.Create(setup);
                await resp.Content.CopyToAsync(fs, ct);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            Log($"{productId}: download do ODT falhou: {ex.Message}");
            return (false, $"Falha no download da Ferramenta de Implantação do Office: {ex.Message}");
        }
        DriverManager.VerifyPublisher(setup, "Microsoft Corporation");

        string xml = Path.Combine(dir, $"{productId}.xml");
        string pid = key != null ? $" PIDKEY=\"{key}\"" : "";
        string autoAct = key != null ? "\n  <Property Name=\"AUTOACTIVATE\" Value=\"1\" />" : "";
        int exit;
        try
        {
            File.WriteAllText(xml, $"""
                <Configuration>
                  <Add OfficeClientEdition="64" Channel="Current">
                    <Product ID="{productId}"{pid}>
                      <Language ID="MatchOS" Fallback="pt-br" />
                    </Product>
                  </Add>
                  <Display Level="None" AcceptEULA="TRUE" />{autoAct}
                </Configuration>
                """, Encoding.UTF8);

            var psi = new ProcessStartInfo(setup, $"/configure \"{xml}\"") { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = dir };
            using var p = Process.Start(psi) ?? throw new InvalidOperationException("Não foi possível iniciar o instalador do Office.");
            try { await p.WaitForExitAsync(ct); }
            catch (OperationCanceledException) { try { p.Kill(true); } catch { } throw; }
            exit = p.ExitCode;
        }
        finally { try { File.Delete(xml); } catch { } } // não deixa a chave no disco
        Log($"{productId}: ODT /configure terminou com 0x{exit:X8} (chave {(key != null ? "informada" : "não informada")}); " +
            $"instalado = {OfficeInstalled(productId)}. Log do Office em %TEMP% (arquivos {Environment.MachineName}-*.log).");

        if (!OfficeInstalled(productId))
            return (false, exit == 0
                ? "O instalador terminou, mas o Office não apareceu como instalado. Tente de novo."
                : $"Falhou (código 0x{exit:X8}). Feche outros instaladores do Office e tente de novo.");
        if (key == null) return (true, "Instalado. Ao abrir o Word ou o Excel, entre com a conta ou a chave do cliente para ativar.");

        // O AUTOACTIVATE às vezes ainda está terminando; se não ativou, tenta pelo OSPP com a mesma chave.
        if (await OfficeLicensedAsync(ct)) return (true, "Instalado e ativado com a chave do cliente.");
        var (ok, msg) = await ActivateOfficeAsync(key, ct);
        return (true, ok ? "Instalado e ativado com a chave do cliente." : $"Instalado, mas a ativação não concluiu: {msg}");
    }

    // ---------------- Ativação do Office (chave do cliente, pela Microsoft) ----------------

    /// <summary>Chave no formato XXXXX-XXXXX-XXXXX-XXXXX-XXXXX (aceita sem hífens); null se inválida.</summary>
    public static string? NormalizeOfficeKey(string? text)
    {
        string k = new string((text ?? "").ToUpperInvariant().Where(char.IsAsciiLetterOrDigit).ToArray());
        if (k.Length != 25) return null;
        return string.Join("-", Enumerable.Range(0, 5).Select(i => k.Substring(i * 5, 5)));
    }

    private static string? OsppPath()
    {
        foreach (var pf in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                                   Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
        {
            string f = Path.Combine(pf, @"Microsoft Office\Office16\OSPP.VBS");
            if (File.Exists(f)) return f;
        }
        return null;
    }

    /// <summary>Roda o OSPP.VBS (script oficial de licenciamento que vem com o Office). A saída não é registrada.</summary>
    private static async Task<(int Exit, string Output)> OsppAsync(string ospp, string args, CancellationToken ct)
    {
        string cscript = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cscript.exe");
        var psi = new ProcessStartInfo(cscript, $"//nologo \"{ospp}\" {args}")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Não foi possível iniciar o OSPP.");
        var outTask = p.StandardOutput.ReadToEndAsync(ct);
        var errTask = p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        return (p.ExitCode, await outTask + await errTask);
    }

    private static async Task<bool> OfficeLicensedAsync(CancellationToken ct)
    {
        if (OsppPath() is not string ospp) return false;
        var (_, output) = await OsppAsync(ospp, "/dstatus", ct);
        return output.Contains("---LICENSED---", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Instala a chave do cliente no Office já instalado e ativa pela internet (OSPP /inpkey + /act).</summary>
    public static async Task<(bool Ok, string Message)> ActivateOfficeAsync(string key, CancellationToken ct = default)
    {
        if (OsppPath() is not string ospp) return (false, "Nenhum Office 2016 ou mais novo foi encontrado neste PC.");
        var (inExit, inOut) = await OsppAsync(ospp, $"/inpkey:{key}", ct);
        if (inExit != 0 || inOut.Contains("ERROR", StringComparison.OrdinalIgnoreCase))
            return (false, "A Microsoft recusou a chave (inválida ou de outra edição do Office).");
        var (_, actOut) = await OsppAsync(ospp, "/act", ct);
        if (await OfficeLicensedAsync(ct)) return (true, "Office ativado.");
        return (false, actOut.Contains("0xC004C008", StringComparison.OrdinalIgnoreCase)
            ? "A chave já foi usada no limite de ativações. O cliente pode ativar pelo telefone ou pela conta Microsoft."
            : "A chave foi aceita, mas a ativação não concluiu. Confira a internet e tente ATIVAR de novo.");
    }

    /// <summary>Última linha útil da saída do winget (sem as barras de progresso).</summary>
    private static string LastLine(string output) =>
        output.Split('\n', '\r').Select(l => l.Trim())
              .LastOrDefault(l => l.Length > 3 && !l.All(c => "-\\|/█▒ ".Contains(c)) && !l.Contains("KB /") && !l.Contains("MB /")) ?? "";
}
