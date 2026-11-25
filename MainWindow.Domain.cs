using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;

namespace ResetExplode;

/// <summary>Aba GLM FERRAMENTAS · Domínio: renomeia no padrão Luft e ingressa no AD.</summary>
public partial class MainWindow
{
    private bool _domainInit, _domainBusy, _domainHome;

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
        BtnDomainJoin.IsEnabled = !_domainHome && cur == null;
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
        if (domain.Length == 0) { DomainStatus.Text = "Informe o domínio."; return null; }
        if (user.Length == 0 || pass.Length == 0) { DomainStatus.Text = "Informe o usuário e a senha de uma conta com permissão no AD."; return null; }
        return (domain, user, pass);
    }

    private static string AdError(Exception ex)
    {
        int hr = ex is COMException c ? c.HResult : ex.HResult;
        return hr switch
        {
            unchecked((int)0x8007052E) => "Usuário ou senha incorretos.",
            unchecked((int)0x80072030) => "Não foi possível ler o AD com essa conta.",
            unchecked((int)0x8007203A) or unchecked((int)0x8007054B) =>
                "O AD não respondeu: confira se o PC está na rede da empresa (cabo ou VPN) e se o DNS aponta para o domínio.",
            _ => ex.Message.Trim()
        };
    }

    private async void BtnDomainFind_Click(object sender, RoutedEventArgs e)
    {
        if (_domainBusy || DomainInputs() is not { } inp) return;
        string prefix = DomainPrefix();
        _domainBusy = true; BtnDomainFind.IsEnabled = BtnDomainJoin.IsEnabled = false;
        DomainStatus.Text = $"Consultando o AD por {prefix}…";
        try
        {
            var (name, count) = await Task.Run(() => DomainJoin.NextName(inp.domain, prefix, inp.user, inp.pass));
            DomainNewName.Text = name;
            DomainStatus.Text = count == 0
                ? $"Nenhum {prefix} no AD ainda. Próximo nome: {name}."
                : $"{count} computador(es) {prefix} no AD. Próximo nome livre: {name}.";
        }
        catch (Exception ex) { DomainStatus.Text = "Falha ao consultar o AD: " + AdError(ex); }
        finally { _domainBusy = false; BtnDomainFind.IsEnabled = true; InitDomain(); }
    }

    private async void BtnDomainJoin_Click(object sender, RoutedEventArgs e)
    {
        if (_domainBusy || DomainInputs() is not { } inp) return;
        string name = DomainNewName.Text.Trim().ToUpperInvariant();
        if (name.Length == 0) { DomainStatus.Text = "Clique em BUSCAR PRÓXIMO NOME primeiro (ou digite o nome)."; return; }
        if (!DomainJoin.IsValidName(name)) { DomainStatus.Text = "Nome inválido: use até 15 letras, números ou hífen."; return; }
        string? ou = string.IsNullOrWhiteSpace(DomainOuBox.Text) ? null : DomainOuBox.Text.Trim();

        if (MessageBox.Show(this,
                $"Renomear esta máquina para {name} e ingressar no domínio {inp.domain}?" +
                (ou != null ? $"\n\nOU: {ou}" : "") + "\n\nDepois é preciso reiniciar.",
                "Ingressar no domínio", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        _domainBusy = true; BtnDomainFind.IsEnabled = BtnDomainJoin.IsEnabled = false;
        try
        {
            DomainStatus.Text = $"Conferindo se {name} está livre no AD…";
            bool taken = await Task.Run(() => DomainJoin.NameExists(inp.domain, name, inp.user, inp.pass));
            if (taken)
            {
                DomainStatus.Text = $"{name} já existe no AD. Clique em BUSCAR PRÓXIMO NOME de novo.";
                return;
            }

            DomainStatus.Text = $"Renomeando para {name} e ingressando em {inp.domain}…";
            await Task.Run(() => DomainJoin.RenameAndJoin(inp.domain, name, ou, inp.user, inp.pass));
            DomainPassBox.Clear();
            DomainStatus.Text = $"Pronto: a máquina entrou no domínio {inp.domain} como {name}. Reinicie para concluir.";

            if (MessageBox.Show(this, $"A máquina entrou no domínio como {name}.\n\nReiniciar agora?",
                    "Ingressar no domínio", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
            {
                try { Process.Start(new ProcessStartInfo("shutdown.exe", "/r /t 10 /c \"GLM INFRA LUFT: concluindo ingresso no domínio\"") { CreateNoWindow = true, UseShellExecute = false }); }
                catch (Exception ex) { DomainStatus.Text = "Reinicie manualmente: " + ex.Message; }
            }
        }
        catch (InvalidOperationException ex) { DomainStatus.Text = ex.Message; }
        catch (Exception ex) { DomainStatus.Text = "Falha: " + AdError(ex); }
        finally { _domainBusy = false; BtnDomainFind.IsEnabled = true; InitDomain(); }
    }
}
