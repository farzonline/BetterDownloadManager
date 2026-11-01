using System.Windows;
using BDM.App.Services;
using BDM.App.Settings;
using BDM.App.ViewModels;
using BDM.App.Views;
using BDM.Core.Api;
using BDM.Core.Data;
using BDM.Core.Engine;
using BDM.Core.Ext;
using BDM.Core.Ipc;
using BDM.Core.Net;
using BDM.Core.Streams;

namespace BDM.App;

public partial class App : Application
{
    public static bool IsExiting { get; private set; }

    SingleInstance? _instance;
    PipeServer? _pipe;
    TrayHost? _tray;
    ClipboardWatcher? _clipboard;
    DropTargetWindow? _drop;
    ConnectivityWatcher? _connectivity;
    DownloadStore? _store;
    MainWindow? _window;
    HostMessageHandler? _handler;
    AppServices? _svc;
    RestApi? _api;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _instance = new SingleInstance("main");
        if (!_instance.IsFirst)
        {
            try { await _instance.ForwardAsync(PipeProtocol.PipeName, e.Args); } catch (Exception) { /* first instance is starting up or gone */ }
            Shutdown();
            return;
        }

        var settings = AppSettings.Load();
        ThemeManager.Initialize(settings.ThemeMode);

        Directory.CreateDirectory(AppSettings.DataDir);
        _store = new DownloadStore(Path.Combine(AppSettings.DataDir, "bdm.db"));
        var vault = new CredentialVault(Path.Combine(AppSettings.DataDir, "vault.json"));
        var profiles = new ProxyProfileStore(Path.Combine(AppSettings.DataDir, "profiles.json"));
        var logins = new SiteLogins(Path.Combine(AppSettings.DataDir, "logins.json"), vault);
        var clients = new ProxyClientProvider(profiles, vault);

        // Read the ffmpeg path live so changing it in Options needs no restart.
        var ffmpeg = new FfmpegLocator(null, _ => FfmpegLocator.Find(settings.FfmpegPath));
        var manager = new DownloadManager(_store, clients.Get)
        {
            StreamsFor = c => new StreamDownloader(c, ffmpeg),
            Logins = logins,
            YtDlp = new YtDlpRunner(() => FfmpegLocator.FindExe("yt-dlp", settings.YtDlpPath), null, () => FfmpegLocator.Find(settings.FfmpegPath)),
            ProxyUrlFor = id => ProxyUrls.For(id, profiles, vault),
        };
        _connectivity = new ConnectivityWatcher();
        manager.Watch(_connectivity);

        _handler = new HostMessageHandler(manager, settings, new QualityPickerService());
        _pipe = new PipeServer(PipeProtocol.PipeName, _handler.HandleAsync);
        _pipe.Start();

        var vm = new MainViewModel(manager, settings, new ExplorerShell(), a => Dispatcher.BeginInvoke(a));
        _svc = new AppServices
        {
            Settings = settings, Manager = manager, Store = _store, Profiles = profiles, Vault = vault, Logins = logins, Clients = clients, Main = vm, Handler = _handler,
            ApplySettings = () => ApplySettings(settings, manager),
        };
        _window = new MainWindow(vm, _svc);
        MainWindow = _window;
        _handler.Activated += () => Dispatcher.BeginInvoke(ShowMainWindow);
        ApplySettings(settings, manager);
        StartLocalApi(settings, manager, vm);

        _tray = new TrayHost(ShowMainWindow, () => Dispatcher.Invoke(() => _window.ShowAddDialog(null)), () => Dispatcher.Invoke(ToggleDropTarget), ExitApp);
        _window.DownloadCompleted += name => { if (!_window.IsVisible) _tray.Notify("Download complete", name); };

        var filter = new ClipboardUrlFilter(() => settings);
        _clipboard = new ClipboardWatcher(_window, filter, () => settings.WatchClipboard);
        _clipboard.UrlCopied += url => Dispatcher.BeginInvoke(() => { if (_window.IsVisible) _window.OfferClipboardUrl(url); });

        // The browser host starts the app with --background so a download request doesn't pop a window up.
        if (!e.Args.Contains("--background")) _window.Show();

        foreach (var arg in e.Args.Where(a => Uri.TryCreate(a, UriKind.Absolute, out var u) && u.Scheme is "http" or "https" or "ftp" or "ftps"))
            await _handler.HandleAsync(new HostMessage("add", arg, null, null, null, null, null, null));
    }

    /// <summary>The token-protected loopback API for the `bdm` tool; its port and token go to api.json in the data folder.</summary>
    void StartLocalApi(AppSettings settings, DownloadManager manager, MainViewModel vm)
    {
        if (!settings.EnableLocalApi) return;
        try
        {
            var token = RestApi.NewToken();
            _api = new RestApi(manager, url => vm.AddUrl(url.ToString()), token, RestApi.FreePort());
            _api.Start();
            ApiInfo.Write(AppSettings.DataDir, _api.Port, token);
        }
        catch (Exception ex) when (ex is System.Net.HttpListenerException or IOException or UnauthorizedAccessException)
        {
            _api = null; // the app works without it; the bdm tool will say the app isn't reachable
        }
    }

    /// <summary>Pushes the saved settings into the engine, the theme and the Windows startup entry.</summary>
    void ApplySettings(AppSettings settings, DownloadManager manager)
    {
        SettingsApplier.Apply(settings, manager, _store!, RunAntivirusAsync);
        ThemeManager.Apply(settings.ThemeMode);
        try { StartupRegistration.Set(settings.StartWithWindows, Environment.ProcessPath!); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException) { /* locked-down PC: leave startup as it was */ }
    }

    async Task RunAntivirusAsync(string command, string path)
    {
        var result = await AntivirusRunner.RunAsync(command, path);
        var problem = result.Error ?? (result.ExitCode is null or 0 ? null : $"The virus scan flagged {Path.GetFileName(path)} (it reported code {result.ExitCode}).");
        if (problem != null) await Dispatcher.BeginInvoke(() => _window?.ShowBanner(problem, error: true));
    }

    void ShowMainWindow()
    {
        if (_window == null) return;
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    public void ToggleDropTarget()
    {
        _drop ??= new DropTargetWindow(url => { try { (_window?.DataContext as MainViewModel)?.AddUrl(url); } catch (ArgumentException) { } });
        if (_drop.IsVisible) _drop.Hide(); else _drop.Show();
    }

    void ExitApp()
    {
        IsExiting = true;
        Dispatcher.Invoke(Shutdown);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _clipboard?.Dispose();
        _tray?.Dispose();
        _pipe?.Dispose();
        if (_api != null) { _api.Dispose(); try { File.Delete(Path.Combine(AppSettings.DataDir, "api.json")); } catch (IOException) { } }
        _connectivity?.Dispose();
        _store?.Dispose();
        _instance?.Dispose();
        base.OnExit(e);
    }
}
