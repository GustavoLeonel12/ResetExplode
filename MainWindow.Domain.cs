using System.Diagnostics;
using System.IO;
using System.Windows;

namespace ResetExplode;

/// <summary>Aba GLM FERRAMENTAS · Domínio: renomeia no padrão Luft e ingressa no AD.</summary>
public partial class MainWindow
{
    private bool _domainInit, _domainBusy, _domainHome;

    private static string DomainLogPath => Path.Combine(AppSettings.Folder, "dominio.log");

    /// <summary>Log da página Domínio. Nunca recebe a senha.</summary>
    private static void DomainLog(string text)
    {
        try
        {
            Directory.CreateDirectory(AppSettings.Folder);
            File.AppendAllText(DomainLogPath, $"[{DateTime.Now:dd/MM/yyyy HH:mm:ss}] {text.TrimEnd()}{Environment.NewLine}", System.Text.Encoding.UTF8);
        }
        catch { }
    }

    private static string Detail(Exception ex)
    {
        var e = ex is AggregateException a && a.InnerException != null ? a.InnerException : ex;
        return $"{e.GetType().Name} 0x{e.HResult:X8}: {e.Message.Trim()}" + (e.InnerException != null ? $" | {e.InnerException.Message.Trim()}" : "");
    }

    /// <summary>Mostra o motivo na tela e numa janela, e grava no log.</summary>
    private void DomainFail(string msg, Exception? ex = null)
    {
        DomainStatus.Text = msg;
        DomainLog("ERRO: " + msg + (ex != null ? "\n    " + Detail(ex) : ""));
        MessageBox.Show(this, msg + (ex != null ? "\n\nDetalhe técnico: " + Detail(ex) : "") + $"\n\nLog: {DomainLogPath}",
            "Ingressar no domínio", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void InitDomain()
    {
        if (_domainBusy) return;
        if (!_domainInit)
        {
            _domainInit = true;
            DomainNameBox.Text = DomainJoin.DefaultDomain;
            string type = DomainJoin.DetectType();
            (type == "NB" ? DomainTypeNb : DomainTypePc).IsChecked = true;
            DomainTypeHint.Text = type == "NB" ? "Detectado: notebook." : "Detectado: desktop.";
            _domainHome = DomainJoin.IsHomeEdition();
        }

        string? cur = DomainJoin.CurrentDomain();
        var warn = new List<string>();
        if (_domainHome)
            warn.Add("Este Windows é Home e não ingressa em domínio. Atualize para Pro, Enterprise ou Education antes.");
        if (cur != null)
            warn.Add($"Esta máquina já está no domínio {cur} como {Environment.MachineName}.");
        DomainWarn.Text = string.Join("\n", warn);
        DomainWarnBox.Visibility = warn.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        BtnDomainJoin.IsEnabled = true; // o clique explica o motivo se não der para ingressar
        if (string.IsNullOrEmpty(DomainStatus.Text))
            DomainStatus.Text = $"Nome atual: {Environment.MachineName}. Preencha a conta e clique em BUSCAR PRÓXIMO NOME.";
    }

    private string DomainPrefix() =>
        (CheckedTag(DomainSegPanel) ?? "LCBAR") + (CheckedTag(DomainTypePanel) ?? "PC");

    private (string domain, string user, string pass)? DomainInputs()
    {
        string domain = DomainNameBox.Text.Trim();
        string user = DomainUserBox.Text.Trim();
        string pass = DomainPassBox.Password;
        if (domain.Length == 0) { DomainFail("Informe o domínio."); return null; }
        if (user.Length == 0 || pass.Length == 0) { DomainFail("Informe o usuário e a senha de uma conta com permissão no AD."); return null; }
        return (domain, user, pass);
    }

    private static string AdError(Exception ex)
    {
        if (ex is AggregateException a && a.InnerException != null) ex = a.InnerException;
        int hr = ex.HResult;
        return hr switch
        {
            unchecked((int)0x8007052E) => "Usuário ou senha incorretos.",
            unchecked((int)0x80072030) => "Não foi possível ler o AD com essa conta.",
            unchecked((int)0x8007203A) or unchecked((int)0x8007054B) =>
                "O AD não respondeu: confira se o PC está na rede da empresa (cabo ou VPN) e se o DNS aponta para o domínio.",
            _ => Detail(ex)
        };
    }

    /// <summary>Consulta o AD e preenche o próximo nome livre. Devolve o nome ou null se falhar.</summary>
    private async Task<string?> FindNextNameAsync((string domain, string user, string pass) inp)
    {
        string prefix = DomainPrefix();
        DomainStatus.Text = $"Consultando o AD por {prefix}…";
        DomainLog($"Buscar próximo nome: domínio {inp.domain}, conta {inp.user}, prefixo {prefix}");
        try
        {
            var (name, count) = await Task.Run(() => DomainJoin.NextName(inp.domain, prefix, inp.user, inp.pass));
            DomainNewName.Text = name;
            DomainStatus.Text = count == 0
                ? $"Nenhum {prefix} no AD ainda. Próximo nome: {name}."
                : $"{count} computador(es) {prefix} no AD. Próximo nome livre: {name}.";
            DomainLog($"  {count} existentes, próximo {name}");
            return name;
        }
        catch (Exception ex) { DomainFail("Falha ao consultar o AD: " + AdError(ex), ex); return null; }
    }

    private async void BtnDomainFind_Click(object sender, RoutedEventArgs e)
    {
        if (_domainBusy || DomainInputs() is not { } inp) return;
        _domainBusy = true; BtnDomainFind.IsEnabled = BtnDomainJoin.IsEnabled = false;
        try { await FindNextNameAsync(inp); }
        catch (Exception ex) { DomainFail("Erro inesperado: " + ex.Message, ex); }
        finally { _domainBusy = false; BtnDomainFind.IsEnabled = true; InitDomain(); }
    }

    private async void BtnDomainJoin_Click(object sender, RoutedEventArgs e)
    {
        if (_domainBusy) return;
        try { await DomainJoinAsync(); }
        catch (Exception ex) { DomainFail("Erro inesperado: " + ex.Message, ex); }
        finally { _domainBusy = false; BtnDomainFind.IsEnabled = true; InitDomain(); }
    }

    private async Task DomainJoinAsync()
    {
        DomainLog($"INGRESSAR clicado. Máquina {Environment.MachineName}");
        if (_domainHome) { DomainFail("Este Windows é Home e não ingressa em domínio. Atualize para Pro, Enterprise ou Education antes."); return; }
        if (DomainJoin.CurrentDomain() is { } cur)
        {
            DomainFail($"Esta máquina já está no domínio {cur} como {Environment.MachineName}. " +
                       "Para ingressar de novo, tire do domínio primeiro (Configurações > Sistema > Sobre > Domínio ou grupo de trabalho) e reinicie.");
            return;
        }
        if (DomainInputs() is not { } inp) return;

        _domainBusy = true; BtnDomainFind.IsEnabled = BtnDomainJoin.IsEnabled = false;
        string name = DomainNewName.Text.Trim().ToUpperInvariant();
        if (name.Length == 0 && await FindNextNameAsync(inp) == null) return;
        name = DomainNewName.Text.Trim().ToUpperInvariant();
        if (!DomainJoin.IsValidName(name)) { DomainFail($"Nome inválido ({name}): use até 15 letras, números ou hífen."); return; }
        string? ou = string.IsNullOrWhiteSpace(DomainOuBox.Text) ? null : DomainOuBox.Text.Trim();

        if (MessageBox.Show(this,
                $"Renomear esta máquina para {name} e ingressar no domínio {inp.domain}?" +
                (ou != null ? $"\n\nOU: {ou}" : "") + "\n\nDepois é preciso reiniciar.",
                "Ingressar no domínio", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            DomainStatus.Text = "Cancelado.";
            return;
        }

        DomainStatus.Text = $"Conferindo se {name} está livre no AD…";
        DomainLog($"Conferindo {name} no AD");
        bool taken;
        try { taken = await Task.Run(() => DomainJoin.NameExists(inp.domain, name, inp.user, inp.pass)); }
        catch (Exception ex) { DomainFail("Falha ao consultar o AD: " + AdError(ex), ex); return; }
        if (taken) { DomainFail($"{name} já existe no AD. Clique em BUSCAR PRÓXIMO NOME de novo."); return; }

        DomainStatus.Text = $"Renomeando para {name} e ingressando em {inp.domain}…";
        DomainLog($"Ingressando: nome {name}, domínio {inp.domain}, OU {ou ?? "(padrão)"}");
        try { await Task.Run(() => DomainJoin.RenameAndJoin(inp.domain, name, ou, inp.user, inp.pass)); }
        catch (Exception ex) { DomainFail(ex is InvalidOperationException ? ex.Message : "Falha ao ingressar: " + AdError(ex), ex); return; }

        DomainPassBox.Clear();
        DomainStatus.Text = $"Pronto: a máquina entrou no domínio {inp.domain} como {name}. Reinicie para concluir.";
        DomainLog($"OK: entrou em {inp.domain} como {name}");

        if (MessageBox.Show(this, $"A máquina entrou no domínio como {name}.\n\nReiniciar agora?",
                "Ingressar no domínio", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
        {
            try { Process.Start(new ProcessStartInfo("shutdown.exe", "/r /t 10 /c \"GLM INFRA LUFT: concluindo ingresso no domínio\"") { CreateNoWindow = true, UseShellExecute = false }); }
            catch (Exception ex) { DomainFail("Reinicie manualmente: " + ex.Message, ex); }
        }
    }
}
