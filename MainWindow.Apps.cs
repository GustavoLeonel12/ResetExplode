using System.Diagnostics;
using System.Windows;

namespace ResetExplode;

/// <summary>Aba GLM FERRAMENTAS · Instalar apps: essenciais pós-formatação pelo winget.</summary>
public partial class MainWindow
{
    private List<InstallableApp>? _installApps;
    private bool _appsBusy, _appsStop;

    private void InitApps()
    {
        if (_appsBusy) return;
        if (_installApps == null)
        {
            _installApps = AppInstaller.Catalog();
            AppsList.ItemsSource = _installApps;
            AppInstaller.RefreshInstalled(_installApps);
            foreach (var a in _installApps) a.Selected = a.PreSelect && !a.Installed;
        }
        else AppInstaller.RefreshInstalled(_installApps);

        bool winget = AppInstaller.WingetAvailable;
        BtnAppsStore.Visibility = winget ? Visibility.Collapsed : Visibility.Visible;
        BtnAppsInstall.IsEnabled = true; // o Office não depende do winget
        int installed = _installApps.Count(a => a.Installed);
        AppsStatus.Text = winget
            ? $"{installed} de {_installApps.Count} já instalados. Marque o que quer e clique em INSTALAR SELECIONADOS."
            : "O winget não foi encontrado. Em PC recém-formatado ele aparece depois que a Microsoft Store atualiza o " +
              "\"Instalador de Aplicativo\": use o botão abaixo, espere atualizar e abra esta página de novo.";
    }

    private void BtnAppsAll_Click(object sender, RoutedEventArgs e)
    {
        if (_installApps == null || _appsBusy) return;
        foreach (var a in _installApps) a.Selected = !a.Installed;
    }

    private void BtnAppsNone_Click(object sender, RoutedEventArgs e)
    {
        if (_installApps == null || _appsBusy) return;
        foreach (var a in _installApps) a.Selected = false;
    }

    private void BtnAppsStore_Click(object sender, RoutedEventArgs e)
    {
        // Página do "Instalador de Aplicativo" (App Installer), que traz o winget.
        try { Process.Start(new ProcessStartInfo("ms-windows-store://pdp/?productid=9NBLGGH4NNS1") { UseShellExecute = true }); }
        catch (Exception ex) { AppsStatus.Text = $"Não foi possível abrir a Microsoft Store: {ex.Message}"; }
    }

    private async void BtnAppsInstall_Click(object sender, RoutedEventArgs e)
    {
        if (_installApps == null) return;
        if (_appsBusy) { _appsStop = true; BtnAppsInstall.IsEnabled = false; AppsStatus.Text = "Parando depois do app atual…"; return; }

        var sel = _installApps.Where(a => a.Selected && !a.Installed).ToList();
        if (sel.Count == 0) { AppsStatus.Text = "Nenhum app selecionado."; return; }
        if (sel.Count(x => x.OfficeProduct != null) > 1)
        {
            MessageBox.Show("Escolha só uma edição do Office (Professional Plus ou Home & Business): a licença do cliente ativa apenas a edição comprada.",
                            "Instalar apps", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        string? officeKey = null;
        if (sel.Any(x => x.OfficeProduct != null) && OfficeKeyBox.Text.Trim().Length > 0)
        {
            officeKey = AppInstaller.NormalizeOfficeKey(OfficeKeyBox.Text);
            if (officeKey == null) { OfficeKeyStatus.Text = "Chave inválida: são 25 letras e números (XXXXX-XXXXX-XXXXX-XXXXX-XXXXX)."; return; }
        }

        AppInstaller.ResetLog();
        _appsBusy = true; _appsStop = false;
        BtnOfficeActivate.IsEnabled = false;
        BtnAppsInstall.Content = "PARAR";
        foreach (var a in sel) a.Status = "Na fila.";
        int ok = 0, done = 0;
        var failed = new List<string>();
        AppsProgress.ScaleX = 0;

        foreach (var app in sel)
        {
            if (_appsStop) { app.Status = "Não instalado (parado)."; continue; }
            AppsStatus.Text = $"Instalando {app.Name} ({done + 1} de {sel.Count})… baixando do site oficial, pode levar alguns minutos.";
            app.Status = app.OfficeProduct != null ? "Baixando e instalando o Office (uns 3 GB, pode levar de 10 a 30 minutos)…" : "Instalando…";
            try
            {
                var (success, msg) = await AppInstaller.InstallAsync(app, CancellationToken.None, officeKey: officeKey);
                app.Status = msg;
                if (success) { ok++; app.Selected = false; }
                else failed.Add(app.Name);
            }
            catch (Exception ex) { app.Status = $"Falhou: {ex.Message}"; failed.Add(app.Name); }
            done++;
            AppsProgress.ScaleX = (double)done / sel.Count;
        }

        AppInstaller.RefreshInstalled(_installApps);
        _appsBusy = false;
        BtnOfficeActivate.IsEnabled = true;
        if (officeKey != null) OfficeKeyBox.Clear(); // não deixa a chave na tela
        BtnAppsInstall.Content = "INSTALAR SELECIONADOS";
        BtnAppsInstall.IsEnabled = true;
        AppsStatus.Text = $"{ok} de {sel.Count} instalados." +
                          (failed.Count > 0 ? $" Falharam: {string.Join(", ", failed)} (motivo na lista; detalhes em {AppInstaller.LogPath})." : "") +
                          (_appsStop ? " Instalação interrompida." : "");
    }

    private async void BtnOfficeActivate_Click(object sender, RoutedEventArgs e)
    {
        if (_appsBusy) return;
        string? key = AppInstaller.NormalizeOfficeKey(OfficeKeyBox.Text);
        if (key == null) { OfficeKeyStatus.Text = "Chave inválida: são 25 letras e números (XXXXX-XXXXX-XXXXX-XXXXX-XXXXX)."; return; }
        BtnOfficeActivate.IsEnabled = false;
        OfficeKeyStatus.Text = "Ativando com a Microsoft…";
        try
        {
            var (ok, msg) = await AppInstaller.ActivateOfficeAsync(key);
            OfficeKeyStatus.Text = msg;
            if (ok) OfficeKeyBox.Clear();
        }
        catch (Exception ex) { OfficeKeyStatus.Text = $"Falhou: {ex.Message}"; }
        finally { BtnOfficeActivate.IsEnabled = true; }
    }
}
