using System.Windows;
using BDM.App.Services;
using BDM.App.ViewModels;

namespace BDM.App.Views;

public partial class GrabberWindow : Window
{
    readonly AppServices _svc;
    readonly GrabberViewModel _vm = new();
    CancellationTokenSource? _cts;

    public GrabberWindow(AppServices svc)
    {
        InitializeComponent();
        ThemeManager.Attach(this);
        Icon = AppIcon.Image();
        _svc = svc;
        DataContext = _vm;
        Closed += (_, _) => _cts?.Cancel();
        Loaded += (_, _) => UrlBox.Focus();
    }

    async void Scan_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        ScanButton.IsEnabled = false;
        ScanButton.Content = "Scanning…";
        try { await _vm.CrawlAsync(_svc.Clients.Get(_svc.Settings.GlobalProxyId), _cts.Token); }
        finally { ScanButton.IsEnabled = true; ScanButton.Content = "Scan"; AddButton.IsEnabled = _vm.Results.Count > 0; }
    }

    void SelectAll_Click(object sender, RoutedEventArgs e) { foreach (var r in _vm.Results) r.IsSelected = true; }
    void SelectNone_Click(object sender, RoutedEventArgs e) { foreach (var r in _vm.Results) r.IsSelected = false; }

    void Add_Click(object sender, RoutedEventArgs e)
    {
        var n = _vm.AddSelected(_svc.Main);
        DialogResult = true;
        (Owner as MainWindow)?.ShowBanner($"Added {n:N0} downloads.");
    }
}
