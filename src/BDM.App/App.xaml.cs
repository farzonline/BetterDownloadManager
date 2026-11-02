using System.Windows;
using BDM.App.Services;
using BDM.App.Settings;
using BDM.App.ViewModels;
using BDM.App.Views;
using BDM.Core;
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
    BrowserRelay? _relay;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _instance = new SingleInstance(PipeProtocol.PipeName); // one app per pipe: a test copy (BDM_PIPE) runs beside the real one
        if (!_instance.IsFirst)
        {
            try { await _instance.ForwardAsync(PipeProtocol.PipeName, e.Args); } catch (Exception) { /* first instance is starting up or gone */ }
            Shutdown();
            return;
        }

        var settings = AppSettings.Load();
        ThemeManager.Initialize(settings.ThemeMode);
        PressFeedback.Register();

        Directory.CreateDirectory(AppSettings.DataDir);
        Log.Configure(Path.Combine(AppSettings.DataDir, "logs"));
        // A display bug must not take every running download down with it: log it, say so, keep going.
        DispatcherUnhandledException += (_, a) =>
        {
            Log.Error("Unhandled UI exception", a.Exception);
            a.Handled = true;
            _window?.ShowBanner($"Something went wrong on screen ({a.Exception.Message}). Your downloads keep running; details are in Help > Open the log folder.", error: true);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, a) => Log.Error("Unhandled exception", a.ExceptionObject as Exception ?? new Exception(a.ExceptionObject?.ToString()));
        TaskScheduler.UnobservedTaskException += (_, a) => Log.Error("Unobserved task exception", a.Exception);
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

        // Sites that only serve a real browser: BDM's engine sends those requests through the extension.
        try
        {
            _relay = new BrowserRelay(RestApi.NewToken(), RestApi.FreePort(), TimeSpan.FromSeconds(30));
            _relay.Start();
            clients.Browser = new HttpClient(_relay.Handler) { Timeout = TimeSpan.FromMinutes(2) };
        }
        catch (Exception ex) when (ex is System.Net.HttpListenerException or IOException)
        {
            Log.Error("The browser relay could not start; protected sites fall back to the browser's own download", ex);
            _relay = null;
        }
        _handler = new HostMessageHandler(manager, settings, new QualityPickerService(), new DownloadPromptService(settings, clients),
            async req =>
            {
                using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                return await BDM.Core.Http.Prober.ProbeAsync(clients.Get(manager.ProxyIdFor(req.Category, "main")), req, limit.Token);
            },
            _relay != null ? new RelayEndpoint(_relay.Port, _relay.Token) : null);
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
        _handler.Started += id => Dispatcher.BeginInvoke(() => _window.ShowProgress(id));
        ApplySettings(settings, manager);
        StartLocalApi(settings, manager, vm);

        _tray = new TrayHost(ShowMainWindow, () => Dispatcher.Invoke(() => _window.ShowAddDialog(null)), () => Dispatcher.Invoke(ToggleDropTarget), ExitApp);
        _window.DownloadCompleted += name => { if (!_window.IsVisible) _tray.Notify("Download complete", name); };

        var filter = new ClipboardUrlFilter(() => settings);
        _clipboard = new ClipboardWatcher(_window, filter, () => settings.WatchClipboard);
        _clipboard.UrlCopied += url => Dispatcher.BeginInvoke(() => _window.OfferClipboardUrl(url));

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

    public void ExitApp()
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
        _relay?.Dispose();
        _store?.Dispose();
        _instance?.Dispose();
        base.OnExit(e);
    }
}
