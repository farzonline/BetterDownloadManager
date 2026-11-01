using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using BDM.App.Services;
using BDM.App.Settings;
using BDM.App.ViewModels;
using BDM.Core.Http;

namespace BDM.App.Views;

public partial class AddUrlDialog : Window
{
    readonly HttpClient _http;
    readonly AppSettings _settings;
    readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(600) };
    CancellationTokenSource? _probe;

    public string Url => UrlBox.Text.Trim();
    public bool StartNow { get; private set; }

    public AddUrlDialog(HttpClient http, AppSettings settings, string? initialUrl)
    {
        InitializeComponent();
        ThemeManager.Attach(this);
        Icon = AppIcon.Image();
        _http = http;
        _settings = settings;
        _debounce.Tick += async (_, _) => { _debounce.Stop(); await ProbeAsync(); };
        UrlBox.Text = initialUrl ?? "";
        UrlBox.SelectAll();
        Loaded += (_, _) => UrlBox.Focus();
    }

    bool Valid(out Uri? uri) =>
        Uri.TryCreate(Url, UriKind.Absolute, out uri) && uri.Scheme is "http" or "https" or "ftp" or "ftps";

    void UrlBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var ok = Valid(out var uri);
        NowButton.IsEnabled = LaterButton.IsEnabled = ok;
        _probe?.Cancel();
        _debounce.Stop();
        if (!ok)
        {
            Info.Text = Url.Length == 0 ? "" : "Enter a full address starting with http://, https:// or ftp://.";
            Folder.Text = "";
            return;
        }
        var category = Categories.For(Categories.NameFromUrl(uri!));
        Folder.Text = Categories.FolderFor(_settings, category);
        Info.Text = "Checking…";
        _debounce.Start();
    }

    async Task ProbeAsync()
    {
        if (!Valid(out var uri) || uri!.Scheme is "ftp" or "ftps") { if (uri?.Scheme is "ftp" or "ftps") Info.Text = "FTP address"; return; }
        _probe = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var token = _probe.Token;
        try
        {
            var req = new DownloadRequest(uri, null, null, new Dictionary<string, string>(), null, null, null, _settings.DownloadRoot);
            var p = await Prober.ProbeAsync(_http, req, token);
            Info.Text = $"{p.FileName}  ·  {(p.Length is { } n ? Format.Size(n) : "size unknown")}  ·  {(p.AcceptsRanges ? "can resume" : "cannot resume")}";
        }
        catch (OperationCanceledException) { /* typed again, or timed out */ if (!token.IsCancellationRequested) return; Info.Text = "No answer from this address yet. You can still add it."; }
        catch (Exception e) when (e is HttpRequestException or BDM.Core.Engine.DownloadException)
        {
            Info.Text = "Couldn't reach this address. You can still add it.";
        }
    }

    void Now_Click(object sender, RoutedEventArgs e) { StartNow = true; DialogResult = true; }
    void Later_Click(object sender, RoutedEventArgs e) { StartNow = false; DialogResult = true; }
}
