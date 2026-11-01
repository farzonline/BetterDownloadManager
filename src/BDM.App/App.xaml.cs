using System.Windows;
using BDM.App.Services;
using BDM.App.Settings;
using BDM.App.ViewModels;
using BDM.App.Views;
using BDM.Core.Data;
using BDM.Core.Engine;
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
        var http = HttpClients.Create(null, vault);
        var manager = new DownloadManager(_store, _ => http)
        {
            // Read the ffmpeg path live so changing it in Options needs no restart.
            Streams = new StreamDownloader(http, new FfmpegLocator(null, _ => FfmpegLocator.Find(settings.FfmpegPath))),
        };
        _connectivity = new ConnectivityWatcher();
        manager.Watch(_connectivity);

        _handler = new HostMessageHandler(manager, settings, new QualityPickerService());
        _pipe = new PipeServer(PipeProtocol.PipeName, _handler.HandleAsync);
        _pipe.Start();

        var vm = new MainViewModel(manager, settings, new ExplorerShell(), a => Dispatcher.BeginInvoke(a));
        _window = new MainWindow(vm, manager, settings, http);
        MainWindow = _window;
        _handler.Activated += () => Dispatcher.BeginInvoke(ShowMainWindow);

        _tray = new TrayHost(ShowMainWindow, () => Dispatcher.Invoke(() => _window.ShowAddDialog(null)), ToggleDropTarget, ExitApp);
        _window.DownloadCompleted += name => { if (!_window.IsVisible) _tray.Notify("Download complete", name); };

        var filter = new ClipboardUrlFilter(() => settings);
        _clipboard = new ClipboardWatcher(_window, filter, () => settings.WatchClipboard);
        _clipboard.UrlCopied += url => Dispatcher.BeginInvoke(() => { if (_window.IsVisible) _window.OfferClipboardUrl(url); });

        // The browser host starts the app with --background so a download request doesn't pop a window up.
        if (!e.Args.Contains("--background")) _window.Show();

        foreach (var arg in e.Args.Where(a => Uri.TryCreate(a, UriKind.Absolute, out var u) && u.Scheme is "http" or "https" or "ftp" or "ftps"))
            await _handler.HandleAsync(new HostMessage("add", arg, null, null, null, null, null, null));
    }

    void ShowMainWindow()
    {
        if (_window == null) return;
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    void ToggleDropTarget()
    {
        Dispatcher.Invoke(() =>
        {
            _drop ??= new DropTargetWindow(url => { try { (_window?.DataContext as MainViewModel)?.AddUrl(url); } catch (ArgumentException) { } });
            if (_drop.IsVisible) _drop.Hide(); else _drop.Show();
        });
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
        _connectivity?.Dispose();
        _store?.Dispose();
        _instance?.Dispose();
        base.OnExit(e);
    }
}
