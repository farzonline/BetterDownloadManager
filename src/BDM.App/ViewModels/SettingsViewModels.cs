using System.Collections.ObjectModel;
using System.Globalization;
using BDM.App.Services;
using BDM.App.Settings;
using BDM.Core.Batch;
using BDM.Core.Data;
using BDM.Core.Engine;
using BDM.Core.Grabber;
using BDM.Core.Net;

namespace BDM.App.ViewModels;

/// <summary>Editable copy of <see cref="AppSettings"/>; nothing changes until <see cref="Apply"/>.</summary>
public sealed class OptionsViewModel : ObservableObject
{
    readonly AppSettings _settings;
    int _connections, _speedKbps;
    string _downloadRoot;
    string? _error;

    public OptionsViewModel(AppSettings s)
    {
        _settings = s;
        _connections = Math.Clamp(s.Connections, 1, 32);
        _speedKbps = Math.Max(0, s.SpeedLimitKBps);
        _downloadRoot = s.DownloadRoot;
        AntivirusCommand = s.AntivirusCommand;
        FfmpegPath = s.FfmpegPath;
        YtDlpPath = s.YtDlpPath;
        ThemeMode = s.ThemeMode;
        AlwaysAskQuality = s.AlwaysAskQuality;
        MinimizeToTray = s.MinimizeToTray;
        WatchClipboard = s.WatchClipboard;
        PasteLinkOnAdd = s.PasteLinkOnAdd;
        ShowDownloadDialog = s.ShowDownloadDialog;
        ShowProgressWindow = s.ShowProgressWindow;
        ShowCompleteDialog = s.ShowCompleteDialog;
        StartWithWindows = s.StartWithWindows;
        EnableLocalApi = s.EnableLocalApi;
        GlobalProxyId = s.GlobalProxyId;
        CategoryFolders = new(s.CategoryFolders);
        CategoryProxies = new(s.CategoryProxies);
    }

    public int Connections { get => _connections; set => Set(ref _connections, Math.Clamp(value, 1, 32)); }
    public int SpeedLimitKBps { get => _speedKbps; set => Set(ref _speedKbps, Math.Max(0, value)); }
    public string DownloadRoot { get => _downloadRoot; set => Set(ref _downloadRoot, value ?? ""); }
    public string? AntivirusCommand { get; set; }
    public string? FfmpegPath { get; set; }
    public string? YtDlpPath { get; set; }
    public string ThemeMode { get; set; }
    public bool AlwaysAskQuality { get; set; }
    public bool MinimizeToTray { get; set; }
    public bool WatchClipboard { get; set; }
    public bool PasteLinkOnAdd { get; set; }
    public bool ShowDownloadDialog { get; set; }
    public bool ShowProgressWindow { get; set; }
    public bool ShowCompleteDialog { get; set; }
    public bool StartWithWindows { get; set; }
    public bool EnableLocalApi { get; set; }
    public string? GlobalProxyId { get; set; }
    public Dictionary<string, string> CategoryFolders { get; }
    public Dictionary<string, string> CategoryProxies { get; }
    public string? Error { get => _error; private set => Set(ref _error, value); }

    /// <returns>false (with <see cref="Error"/> set) when something is invalid.</returns>
    public bool Apply()
    {
        if (string.IsNullOrWhiteSpace(DownloadRoot)) { Error = "Choose a folder to save downloads in."; return false; }
        Error = null;
        _settings.Connections = Connections;
        _settings.SpeedLimitKBps = SpeedLimitKBps;
        _settings.DownloadRoot = DownloadRoot.Trim();
        _settings.AntivirusCommand = string.IsNullOrWhiteSpace(AntivirusCommand) ? null : AntivirusCommand.Trim();
        _settings.FfmpegPath = string.IsNullOrWhiteSpace(FfmpegPath) ? null : FfmpegPath.Trim();
        _settings.YtDlpPath = string.IsNullOrWhiteSpace(YtDlpPath) ? null : YtDlpPath.Trim();
        _settings.ThemeMode = ThemeMode;
        _settings.AlwaysAskQuality = AlwaysAskQuality;
        _settings.MinimizeToTray = MinimizeToTray;
        _settings.WatchClipboard = WatchClipboard;
        _settings.PasteLinkOnAdd = PasteLinkOnAdd;
        _settings.ShowDownloadDialog = ShowDownloadDialog;
        _settings.ShowProgressWindow = ShowProgressWindow;
        _settings.ShowCompleteDialog = ShowCompleteDialog;
        _settings.StartWithWindows = StartWithWindows;
        _settings.EnableLocalApi = EnableLocalApi;
        _settings.GlobalProxyId = string.IsNullOrWhiteSpace(GlobalProxyId) ? null : GlobalProxyId;
        _settings.CategoryFolders = CategoryFolders.Where(kv => !string.IsNullOrWhiteSpace(kv.Value)).ToDictionary(kv => kv.Key, kv => kv.Value.Trim());
        _settings.CategoryProxies = CategoryProxies.Where(kv => !string.IsNullOrWhiteSpace(kv.Value)).ToDictionary(kv => kv.Key, kv => kv.Value);
        return true;
    }
}

/// <summary>Proxy / VPN profiles. Passwords go to the credential vault only.</summary>
public sealed class ProxyProfilesViewModel
{
    readonly ProxyProfileStore _store;
    readonly CredentialVault _vault;

    public ProxyProfilesViewModel(ProxyProfileStore store, CredentialVault vault)
    {
        _store = store;
        _vault = vault;
        Items = new ObservableCollection<ProxyProfile>(store.Load());
    }

    public ObservableCollection<ProxyProfile> Items { get; }

    void Save() => _store.Save(Items);

    public ProxyProfile Add(string name, ProxyKind kind, string host, int port, string? username, string? password)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Give the profile a name.", nameof(name));
        if (string.IsNullOrWhiteSpace(host)) throw new ArgumentException("Enter the proxy's host name.", nameof(host));
        if (port is < 1 or > 65535) throw new ArgumentException("The port must be between 1 and 65535.", nameof(port));
        var p = new ProxyProfile(Guid.NewGuid().ToString("N")[..8], name.Trim(), kind, host.Trim(), port, string.IsNullOrWhiteSpace(username) ? null : username.Trim());
        Items.Add(p);
        if (!string.IsNullOrEmpty(password)) _vault.Save(p.VaultKey, password);
        Save();
        return p;
    }

    /// <summary>Adds the servers from a pasted Surfshark list, all sharing the one set of service credentials. Returns how many were new.</summary>
    public int ImportSurfshark(string text, string? username, string? password)
    {
        var added = 0;
        foreach (var parsed in SurfsharkImport.Parse(text))
        {
            if (Items.Any(i => i.Id == parsed.Id)) continue;
            var p = parsed with { Username = string.IsNullOrWhiteSpace(username) ? null : username.Trim() };
            Items.Add(p);
            if (!string.IsNullOrEmpty(password)) _vault.Save(p.VaultKey, password);
            added++;
        }
        if (added > 0) Save();
        return added;
    }

    public void SetPassword(ProxyProfile p, string password) => _vault.Save(p.VaultKey, password);

    public void Remove(ProxyProfile p)
    {
        Items.Remove(p);
        _vault.Remove(p.VaultKey);
        Save();
    }
}

public sealed class SitesLoginViewModel
{
    readonly SiteLogins _logins;

    public SitesLoginViewModel(SiteLogins logins)
    {
        _logins = logins;
        Items = new ObservableCollection<SiteLogin>(logins.All());
    }

    public ObservableCollection<SiteLogin> Items { get; }

    /// <summary>Accepts a bare host or a pasted address; only the host is kept.</summary>
    public static string NormalizeHost(string input)
    {
        var text = (input ?? "").Trim();
        if (text.Length == 0) return "";
        if (Uri.TryCreate(text.Contains("://") ? text : "https://" + text, UriKind.Absolute, out var u)) return u.Host.ToLowerInvariant();
        return "";
    }

    public void Save(string hostOrUrl, string username, string password)
    {
        var host = NormalizeHost(hostOrUrl);
        if (host.Length == 0) throw new ArgumentException("Enter the site's address, for example files.example.com.", nameof(hostOrUrl));
        if (string.IsNullOrWhiteSpace(username)) throw new ArgumentException("Enter the user name.", nameof(username));
        _logins.Set(host, username.Trim(), password ?? "");
        Reload();
    }

    public void Remove(SiteLogin login) { _logins.Remove(login.Host); Reload(); }

    void Reload()
    {
        Items.Clear();
        foreach (var l in _logins.All().OrderBy(l => l.Host)) Items.Add(l);
    }
}

public sealed class QueueEditor : ObservableObject
{
    string _startAt = "", _stopAt = "";
    string? _error;

    public QueueEditor(QueueDefinition q)
    {
        Id = q.Id; Name = q.Name; MaxConcurrent = q.MaxConcurrent; Connections = q.Connections;
        _startAt = q.StartAt is { } a ? a.ToString(@"hh\:mm") : "";
        _stopAt = q.StopAt is { } b ? b.ToString(@"hh\:mm") : "";
        OnFinish = q.OnFinish; ProxyProfileId = q.ProxyProfileId;
    }

    public string Id { get; }
    public string Name { get; set; }
    public int MaxConcurrent { get; set; }
    public int Connections { get; set; }
    public string StartAtText { get => _startAt; set => Set(ref _startAt, value ?? ""); }
    public string StopAtText { get => _stopAt; set => Set(ref _stopAt, value ?? ""); }
    public PostAction OnFinish { get; set; }
    public string? ProxyProfileId { get; set; }
    public string? Error { get => _error; set => Set(ref _error, value); }
}

public sealed class QueuesViewModel
{
    readonly DownloadStore _store;

    public QueuesViewModel(DownloadStore store)
    {
        _store = store;
        Items = new ObservableCollection<QueueEditor>(store.Queues().Select(q => new QueueEditor(q)));
    }

    public ObservableCollection<QueueEditor> Items { get; }

    public QueueEditor Add(string name)
    {
        var q = new QueueDefinition(Guid.NewGuid().ToString("N")[..8], string.IsNullOrWhiteSpace(name) ? "New queue" : name.Trim(), 3, 8, null, null, PostAction.None);
        _store.SaveQueue(q);
        var editor = new QueueEditor(q);
        Items.Add(editor);
        return editor;
    }

    static bool TryTime(string text, out TimeSpan? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(text)) return true;
        if (!TimeSpan.TryParseExact(text.Trim(), [@"h\:mm", @"hh\:mm"], CultureInfo.InvariantCulture, out var t)) return false;
        value = t;
        return true;
    }

    public bool Save(QueueEditor e)
    {
        if (!TryTime(e.StartAtText, out var start)) { e.Error = "Start time must look like 23:00."; return false; }
        if (!TryTime(e.StopAtText, out var stop)) { e.Error = "Stop time must look like 06:30."; return false; }
        e.Error = null;
        _store.SaveQueue(new QueueDefinition(e.Id, string.IsNullOrWhiteSpace(e.Name) ? "Queue" : e.Name.Trim(), Math.Clamp(e.MaxConcurrent, 1, 16),
            Math.Clamp(e.Connections, 1, 32), start, stop, e.OnFinish, string.IsNullOrWhiteSpace(e.ProxyProfileId) ? null : e.ProxyProfileId));
        return true;
    }

    /// <returns>false for the main queue or a queue that still holds downloads.</returns>
    public bool Delete(QueueEditor e, DownloadManager manager)
    {
        if (e.Id == "main" || manager.All().Any(r => r.QueueId == e.Id)) return false;
        _store.DeleteQueue(e.Id);
        Items.Remove(e);
        return true;
    }
}

public sealed class BatchViewModel : ObservableObject
{
    string _pattern = "";
    int _count;
    string? _error;
    IReadOnlyList<Uri> _expanded = [];

    public string Pattern { get => _pattern; set { if (Set(ref _pattern, value ?? "")) Recompute(); } }
    public int Count { get => _count; private set => Set(ref _count, value); }
    public string? Error { get => _error; private set => Set(ref _error, value); }
    public IReadOnlyList<string> PreviewLines => _expanded.Take(5).Select(u => u.ToString()).ToList();

    void Recompute()
    {
        try
        {
            var uris = string.IsNullOrWhiteSpace(_pattern) ? [] : BatchPattern.Expand(_pattern.Trim());
            if (uris.Any(u => u.Scheme is not ("http" or "https" or "ftp" or "ftps"))) throw new UriFormatException();
            _expanded = uris;
            Error = null;
        }
        catch (ArgumentException e) { _expanded = []; Error = e.Message; }
        catch (UriFormatException) { _expanded = []; Error = "Enter a full address starting with http://, https:// or ftp://."; }
        Count = _expanded.Count;
        Raise(nameof(PreviewLines));
    }

    public int AddAll(MainViewModel main)
    {
        var n = 0;
        foreach (var u in _expanded) { main.AddUrl(u.ToString()); n++; }
        return n;
    }
}

public sealed class GrabResult : ObservableObject
{
    bool _selected = true;
    public GrabResult(Uri url) { Url = url; Name = Categories.NameFromUrl(url); }
    public Uri Url { get; }
    public string Name { get; }
    public bool IsSelected { get => _selected; set => Set(ref _selected, value); }
}

public sealed class GrabberViewModel : ObservableObject
{
    string _startUrl = "", _include = "", _exclude = "";
    string? _error;
    bool _busy;

    public string StartUrl { get => _startUrl; set => Set(ref _startUrl, value ?? ""); }
    public int Depth { get; set; } = 1;
    public string IncludeExtensions { get => _include; set => Set(ref _include, value ?? ""); }
    public string ExcludeExtensions { get => _exclude; set => Set(ref _exclude, value ?? ""); }
    public bool SameHostOnly { get; set; } = true;
    public bool IsBusy { get => _busy; private set => Set(ref _busy, value); }
    public string? Error { get => _error; private set => Set(ref _error, value); }
    public ObservableCollection<GrabResult> Results { get; } = new();

    static string[] Split(string s) => s.Split([' ', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public async Task CrawlAsync(HttpClient http, CancellationToken ct)
    {
        Results.Clear();
        Error = null;
        if (!Uri.TryCreate(StartUrl.Trim(), UriKind.Absolute, out var start) || start.Scheme is not ("http" or "https"))
        {
            Error = "Enter the page to scan, starting with http:// or https://.";
            return;
        }
        IsBusy = true;
        try
        {
            var found = await SiteGrabber.CrawlAsync(start, new GrabOptions(Math.Clamp(Depth, 0, 5), Split(IncludeExtensions), Split(ExcludeExtensions), SameHostOnly), http, ct);
            foreach (var u in found) Results.Add(new GrabResult(u));
            if (found.Count == 0) Error = "No matching files were found on that page.";
        }
        catch (OperationCanceledException) { Error = "Stopped."; }
        catch (HttpRequestException e) { Error = "Couldn't read that page: " + e.Message; }
        finally { IsBusy = false; }
    }

    public int AddSelected(MainViewModel main)
    {
        var n = 0;
        foreach (var r in Results.Where(r => r.IsSelected)) { main.AddUrl(r.Url.ToString()); n++; }
        return n;
    }
}

public sealed class HistoryViewModel : ObservableObject
{
    readonly DownloadStore _store;
    string _search = "";

    public HistoryViewModel(DownloadStore store) { _store = store; Refresh(); }

    public ObservableCollection<DownloadItemViewModel> Results { get; } = new();

    public string SearchText { get => _search; set { if (Set(ref _search, value ?? "")) Refresh(); } }

    public void Refresh()
    {
        Results.Clear();
        var now = DateTimeOffset.UtcNow;
        foreach (var r in _store.Search(_search.Trim()).OrderByDescending(r => r.Added)) Results.Add(new DownloadItemViewModel(r, now));
    }
}
