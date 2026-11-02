using System.Windows;
using BDM.App.Services;
using BDM.App.Settings;
using BDM.App.ViewModels;
using BDM.Core.Data;

namespace BDM.App.Views;

/// <summary>IDM's "Download complete" window: what was saved where, and Open / Open with / Open folder.</summary>
public partial class CompleteDialog : Window
{
    readonly string _path;
    readonly AppSettings _settings;

    public CompleteDialog(DownloadRecord rec, AppSettings settings)
    {
        InitializeComponent();
        ThemeManager.Attach(this);
        Icon = AppIcon.Image();
        _settings = settings;
        _path = rec.FilePath ?? "";
        var name = Path.GetFileName(_path);
        Title = $"Download complete · {name}";
        Header.Subtitle = name;
        UrlText.Text = rec.Request.Url.ToString();
        UrlText.ToolTip = UrlText.Text;
        PathText.Text = _path;
        PathText.ToolTip = _path;
        SizeText.Text = rec.Total is > 0 ? Format.Size(rec.Total.Value) : File.Exists(_path) ? Format.Size(new FileInfo(_path).Length) : "Unknown";
        Closed += (_, _) =>
        {
            if (DontShow.IsChecked != true) return;
            _settings.ShowCompleteDialog = false;
            try { _settings.Save(); } catch (IOException) { /* off for this session */ }
        };
    }

    void Open_Click(object sender, RoutedEventArgs e) { new ExplorerShell().Open(_path); Close(); }
    void Folder_Click(object sender, RoutedEventArgs e) { new ExplorerShell().ShowInFolder(_path); Close(); }
    void Close_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>Windows' own "Open with" chooser.</summary>
    void OpenWith_Click(object sender, RoutedEventArgs e)
    {
        if (!File.Exists(_path)) { new ExplorerShell().Open(_path); return; } // says the file is gone
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("rundll32.exe", $"shell32.dll,OpenAs_RunDLL {_path}") { UseShellExecute = false });
        Close();
    }
}
