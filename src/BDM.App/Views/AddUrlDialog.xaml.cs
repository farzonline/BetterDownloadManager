using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using BDM.App.Services;
using BDM.App.Settings;
using BDM.App.ViewModels;
using BDM.Core.Http;

namespace BDM.App.Views;

/// <summary>
/// IDM's "Download File Info": address, category, where to save, a note, then start now or later. Used for Add URL,
/// copied links and downloads from the browser (then the address is fixed and the browser's headers are used to look it up).
/// </summary>
public partial class AddUrlDialog : Window
{
    readonly HttpClient _http;
    readonly AppSettings _settings;
    readonly DownloadRequest? _template;
    readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(600) };
    CancellationTokenSource? _probe;
    string _autoName = "download";
    bool _settingPath;
    bool _nameEdited;

    public string Url => UrlBox.Text.Trim();
    /// <summary>The address is a web page whose video yt-dlp should find, not a file.</summary>
    public bool UseYtDlp => PageBox.IsChecked == true;
    /// <summary>Set when the owner chose Start download or Download later.</summary>
    public DownloadPlan? Plan { get; private set; }

    /// <param name="template">A download caught in the browser: its address is fixed and its headers are used for the lookup.</param>
    public AddUrlDialog(HttpClient http, AppSettings settings, string? initialUrl, IReadOnlyList<ProxyChoice> proxies, DownloadPlan? proposal = null, DownloadRequest? template = null)
    {
        InitializeComponent();
        ThemeManager.Attach(this);
        Icon = AppIcon.Image();
        _http = http;
        _settings = settings;
        _template = template;
        ProxyBox.ItemsSource = proxies;
        ProxyBox.SelectedIndex = 0;
        CategoryBox.ItemsSource = Categories.Types;
        _debounce.Tick += async (_, _) => { _debounce.Stop(); await ProbeAsync(); };

        if (template != null)
        {
            UrlBox.IsReadOnly = true;
            PageBox.Visibility = Visibility.Collapsed;
            Header.Subtitle = "Caught in your browser. Check where it goes, then start it now or later.";
        }
        if (initialUrl == null && proposal == null && settings.PasteLinkOnAdd) initialUrl = CopiedLink()?.ToString();
        UrlBox.Text = proposal?.Url.ToString() ?? initialUrl ?? "";
        if (proposal != null)
        {
            CategoryBox.SelectedItem = proposal.Category;
            SetPath(proposal.Folder, proposal.FileName ?? proposal.SuggestedName);
            _nameEdited = proposal.FileName != null; // a video named after its page keeps that name when the server suggests another
            DescriptionBox.Text = proposal.Description ?? "";
        }
        UrlBox.SelectAll();
        Loaded += (_, _) => { if (template != null) DescriptionBox.Focus(); else UrlBox.Focus(); };
    }

    static Uri? CopiedLink()
    {
        try { return Clipboard.ContainsText() ? DownloadPlan.LinkFrom(Clipboard.GetText()) : null; }
        catch (System.Runtime.InteropServices.ExternalException) { return null; } // another app holds the clipboard
    }

    bool Valid(out Uri? uri) => (uri = DownloadPlan.LinkFrom(Url)) != null;

    string Category => CategoryBox.SelectedItem as string ?? "Other";

    void SetPath(string folder, string name)
    {
        _settingPath = true;
        PathBox.Text = Path.Combine(folder, name);
        PathBox.CaretIndex = PathBox.Text.Length; // show the file name end of a long path (after layout)
        Dispatcher.BeginInvoke(() => PathBox.ScrollToHorizontalOffset(double.MaxValue), DispatcherPriority.Loaded);
        _settingPath = false;
    }

    (string Folder, string Name) SplitPath()
    {
        var p = PathBox.Text.Trim();
        return (Path.GetDirectoryName(p) ?? "", Path.GetFileName(p));
    }

    void UrlBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var ok = Valid(out var uri);
        NowButton.IsEnabled = LaterButton.IsEnabled = ok;
        _probe?.Cancel();
        _debounce.Stop();
        if (!ok)
        {
            Info.Text = Url.Length == 0 ? "Paste a link to a file, or a video page." : "Enter a full address starting with http://, https:// or ftp://.";
            return;
        }
        _autoName = Categories.NameFromUrl(uri!);
        if (!_nameEdited)
        {
            var category = Categories.For(_autoName);
            CategoryBox.SelectedItem = category; // also moves the path to that category's folder
            SetPath(Categories.FolderFor(_settings, category), _autoName);
        }
        Info.Text = "Checking…";
        _debounce.Start();
    }

    void CategoryBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RememberBox.Content = $"Remember this folder for “{Category}”";
        var (_, name) = SplitPath();
        SetPath(Categories.FolderFor(_settings, Category), name.Length > 0 ? name : _autoName);
    }

    void PathBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_settingPath) return;
        _nameEdited = SplitPath().Name != _autoName;
    }

    void PageBox_Changed(object sender, RoutedEventArgs e)
    {
        // yt-dlp names the file from the page and files it under Video; the other fields don't apply.
        var file = PageBox.IsChecked != true;
        CategoryBox.IsEnabled = PathBox.IsEnabled = RememberBox.IsEnabled = DescriptionBox.IsEnabled = file;
    }

    void Browse_Click(object sender, RoutedEventArgs e)
    {
        var (folder, name) = SplitPath();
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save the download as", FileName = name, OverwritePrompt = false,
            InitialDirectory = Directory.Exists(folder) ? folder : _settings.DownloadRoot,
        };
        if (dlg.ShowDialog(this) == true) PathBox.Text = dlg.FileName;
    }

    async Task ProbeAsync()
    {
        if (!Valid(out var uri) || uri!.Scheme is "ftp" or "ftps") { if (uri?.Scheme is "ftp" or "ftps") Info.Text = "FTP address"; return; }
        _probe = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var token = _probe.Token;
        try
        {
            var req = _template ?? new DownloadRequest(uri, null, null, new Dictionary<string, string>(), null, null, null, _settings.DownloadRoot);
            var p = await Prober.ProbeAsync(_http, req, token);
            Info.Text = $"{(p.Length is { } n ? Format.Size(n) : "Size unknown")}  ·  {(p.AcceptsRanges ? "can pause and resume" : "cannot resume if stopped")}";
            if (!_nameEdited && !string.IsNullOrWhiteSpace(p.FileName) && p.FileName != _autoName)
            {
                _autoName = p.FileName;
                var category = Categories.For(_autoName);
                CategoryBox.SelectedItem = category;
                SetPath(Categories.FolderFor(_settings, category), _autoName);
            }
        }
        catch (OperationCanceledException) { if (!token.IsCancellationRequested) return; Info.Text = "No answer from this address yet. You can still add it."; }
        catch (Exception e) when (e is HttpRequestException or BDM.Core.Engine.DownloadException)
        {
            Info.Text = "Couldn't look this address up. You can still add it.";
        }
    }

    void Now_Click(object sender, RoutedEventArgs e) => Finish(startNow: true);
    void Later_Click(object sender, RoutedEventArgs e) => Finish(startNow: false);

    void Finish(bool startNow)
    {
        if (!Valid(out var uri)) return;
        var (folder, name) = SplitPath();
        if (!UseYtDlp && (folder.Length == 0 || name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
        {
            Info.Text = "Choose a folder and a file name to save as.";
            PathBox.Focus();
            return;
        }
        if (RememberBox.IsChecked == true && !UseYtDlp)
        {
            Categories.Remember(_settings, Category, folder);
            try { _settings.Save(); } catch (IOException) { /* still remembered for this session */ }
        }
        Plan = new DownloadPlan(uri!, _autoName, name == _autoName ? null : name, folder, Category,
            DescriptionBox.Text, startNow, ProxyBox.SelectedValue as string);
        DialogResult = true;
    }
}
