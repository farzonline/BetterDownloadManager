using System.Windows;
using System.Windows.Controls;
using BDM.App.Services;
using BDM.App.ViewModels;
using Microsoft.Win32;

namespace BDM.App.Views;

public partial class OptionsWindow : Window
{
    readonly AppServices _svc;
    readonly OptionsViewModel _vm;
    static readonly string[] FolderCategories = ["Compressed", "Documents", "Music", "Programs", "Video", "Other"];

    public OptionsWindow(AppServices svc)
    {
        InitializeComponent();
        ThemeManager.Attach(this);
        Icon = AppIcon.Image();
        _svc = svc;
        _vm = new OptionsViewModel(svc.Settings);
        foreach (var c in FolderCategories) _vm.CategoryFolders.TryAdd(c, ""); // so the indexer bindings always have a key
        DataContext = _vm;
        FillProxyChoices();
    }

    void FillProxyChoices()
    {
        ProxyBox.ItemsSource = _svc.Clients.Choices("No proxy (direct)");
        ProxyBox.SelectedValue = _vm.GlobalProxyId ?? "";
    }

    void Save_Click(object sender, RoutedEventArgs e)
    {
        ErrorText.Text = "";
        if (!_vm.Apply()) { ErrorText.Text = _vm.Error; return; }
        try { _svc.Settings.Save(); }
        catch (IOException ex) { ErrorText.Text = "Couldn't save settings: " + ex.Message; return; }
        _svc.ApplySettings();
        DialogResult = true;
    }

    void BrowseRoot_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Save downloads to", InitialDirectory = Directory.Exists(RootBox.Text) ? RootBox.Text : null };
        if (dlg.ShowDialog(this) == true) RootBox.Text = dlg.FolderName;
    }

    void BrowseCategory_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Choose a folder" };
        if (dlg.ShowDialog(this) != true) return;
        // The dictionary has no change notification, so write through the row's text box (its binding updates the view model).
        if (((Button)sender).Parent is Grid row) row.Children.OfType<TextBox>().First().Text = dlg.FolderName;
    }

    void BrowseFfmpeg_Click(object sender, RoutedEventArgs e) => PickProgram(FfmpegBox, "ffmpeg (ffmpeg.exe)|ffmpeg.exe|Programs (*.exe)|*.exe");
    void BrowseYtDlp_Click(object sender, RoutedEventArgs e) => PickProgram(YtDlpBox, "yt-dlp (yt-dlp.exe)|yt-dlp.exe|Programs (*.exe)|*.exe");

    void PickProgram(TextBox target, string filter)
    {
        var dlg = new OpenFileDialog { Filter = filter, CheckFileExists = true };
        if (dlg.ShowDialog(this) == true) target.Text = dlg.FileName;
    }

    void ManageProfiles_Click(object sender, RoutedEventArgs e)
    {
        var keep = _vm.GlobalProxyId;
        new ProxyProfilesWindow(_svc) { Owner = this }.ShowDialog();
        _vm.GlobalProxyId = keep;
        FillProxyChoices();
    }

    void Export_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog { Filter = "BDM settings (*.bdmsettings)|*.bdmsettings", FileName = "bdm-backup.bdmsettings" };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            File.WriteAllText(dlg.FileName, SettingsBundle.Export(_svc.Settings, _svc.Profiles.Load(), _svc.Logins.All(), _svc.Store));
            InfoText.Text = "Exported.";
        }
        catch (IOException ex) { ErrorText.Text = "Couldn't write the file: " + ex.Message; }
    }

    void Import_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "BDM settings (*.bdmsettings)|*.bdmsettings|All files|*.*", CheckFileExists = true };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            var result = SettingsBundle.Import(File.ReadAllText(dlg.FileName), _svc.Settings, _svc.Profiles, _svc.Store);
            _svc.Settings.Save();
            _svc.Manager.ReloadFromStore();
            _svc.Clients.Invalidate();
            _svc.ApplySettings();
            DialogResult = true;
            var note = result.LoginHosts.Length > 0 ? $" Re-enter the passwords for {string.Join(", ", result.LoginHosts)} and any proxy profiles." : "";
            (Owner as MainWindow)?.ShowBanner("Settings imported." + note);
        }
        catch (Exception ex) when (ex is FormatException or IOException) { ErrorText.Text = ex.Message; }
    }
}
