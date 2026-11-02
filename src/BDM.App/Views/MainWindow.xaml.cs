using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using BDM.App.Services;
using BDM.App.Settings;
using BDM.App.ViewModels;
using BDM.Core.Data;
using BDM.Core.Engine;

namespace BDM.App.Views;

public partial class MainWindow : Window
{
    readonly MainViewModel _vm;
    readonly DownloadManager _manager;
    readonly AppSettings _settings;
    readonly AppServices _svc;
    readonly Dictionary<Guid, DownloadState> _lastStates = new();
    readonly Dictionary<Guid, ProgressDialog> _progress = new();
    AddUrlDialog? _openAdd;
    GridLength _treeWidth = new(230);

    /// <summary>Raised on the UI thread when a download finishes (the app shows a tray notification if the window is hidden).</summary>
    public event Action<string>? DownloadCompleted;

    public MainWindow(MainViewModel vm, AppServices svc)
    {
        InitializeComponent();
        _vm = vm;
        _svc = svc;
        var manager = _manager = svc.Manager;
        _settings = svc.Settings;
        DataContext = vm;
        Crest.Source = AppIcon.Image(32);
        Icon = AppIcon.Image();
        ThemeManager.Attach(this);

        foreach (var r in manager.All()) _lastStates[r.Id] = r.State;
        manager.Changed += r => Dispatcher.BeginInvoke(() => OnRecordChanged(r));
        vm.AddUrlRequested += () => ShowAddDialog(null);
        vm.RemoveRequested += ConfirmRemove;
    }

    // ---------------------------------------------------------------- add / remove

    /// <summary>The download dialog (IDM's "Download File Info"). One at a time: a second copied link while it is open is ignored.</summary>
    public async void ShowAddDialog(string? initialUrl)
    {
        if (_openAdd != null) { _openAdd.Activate(); return; }
        var proposal = initialUrl != null && DownloadPlan.LinkFrom(initialUrl) is { } u ? DownloadPlan.Propose(u, _settings) : null;
        var dlg = new AddUrlDialog(_svc.Clients.Get(null), _settings, initialUrl, _svc.Clients.Choices("Use the default"), proposal);
        DialogPlacement.Place(dlg);
        _openAdd = dlg;
        bool ok;
        try { ok = dlg.ShowDialog() == true; } finally { _openAdd = null; }
        if (!ok) return;
        try
        {
            if (dlg.UseYtDlp)
            {
                var reply = await _svc.Handler.HandleAsync(new BDM.Core.Ipc.HostMessage("addPageAsk", dlg.Url, null, null, null, null, null, null));
                if (!reply.Ok) ShowBanner(reply.Error ?? "yt-dlp couldn't use that page.", error: true);
                return;
            }
            var plan = dlg.Plan!;
            var id = _vm.Add(plan);
            if (plan.StartNow) ShowProgress(id);
        }
        catch (ArgumentException e) { ShowBanner(e.Message, error: true); }
    }

    /// <summary>The download's own progress window (one per download, as in IDM), if the owner wants them.</summary>
    public void ShowProgress(Guid id, bool always = false)
    {
        if (!always && !_settings.ShowProgressWindow) return;
        if (_progress.TryGetValue(id, out var open)) { open.BringBack(); return; }
        if (_manager.Get(id) is not { } rec) return;
        if (rec.State == DownloadState.Completed) { ShowComplete(rec, null); return; }
        var w = new ProgressDialog(_manager, id);
        _progress[id] = w;
        w.Closed += (_, _) => _progress.Remove(id);
        w.Show();
    }

    /// <summary>IDM's "Download complete" window, where the progress window was (or centred, in front).</summary>
    void ShowComplete(DownloadRecord rec, Point? at)
    {
        var d = new CompleteDialog(rec, _settings);
        DialogPlacement.Place(d);
        if (at is { } p) { d.WindowStartupLocation = WindowStartupLocation.Manual; d.Left = p.X; d.Top = p.Y; }
        d.Show();
    }

    void ConfirmRemove()
    {
        var n = _vm.SelectedItems.Count;
        if (n == 0) return;
        var what = n == 1 ? $"“{_vm.SelectedItems[0].Name}”" : $"{n} downloads";
        var (ok, alsoFiles) = ConfirmDialog.Ask(this, $"Remove {what}?", "They leave the list. Files you already downloaded stay on disk unless you choose otherwise.",
            "Remove", destructive: true, optionLabel: "Also delete the downloaded files");
        if (ok) _vm.RemoveSelected(alsoFiles);
    }

    // ---------------------------------------------------------------- banner (inline messages, never dialogs for success)

    public void ShowBanner(string text, bool error = false, params (string Label, Action Act)[] actions)
    {
        var c = (Color)FindResource(error ? "Color.Danger" : "Color.AccentText");
        Banner.Background = new SolidColorBrush(Color.FromArgb(31, c.R, c.G, c.B));      // state colour at ~12 %
        Banner.BorderBrush = new SolidColorBrush(Color.FromArgb(89, c.R, c.G, c.B));     // border at ~35 %
        BannerIcon.Text = error ? "" : "";
        BannerIcon.Foreground = new SolidColorBrush(c);
        BannerText.Text = text;
        BannerText.ToolTip = text;
        BannerActions.Children.Clear();
        foreach (var (label, act) in actions)
        {
            var b = new Button { Content = label, MinHeight = 28, Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(12, 0, 12, 0) };
            b.Click += (_, _) => { act(); Banner.Visibility = Visibility.Collapsed; };
            BannerActions.Children.Add(b);
        }
        Banner.Visibility = Visibility.Visible;
    }

    void BannerClose_Click(object sender, RoutedEventArgs e) => Banner.Visibility = Visibility.Collapsed;

    /// <summary>A download link was copied somewhere: open the download dialog for it (IDM's clipboard catching).</summary>
    public void OfferClipboardUrl(Uri url) => ShowAddDialog(url.ToString());

    void OnRecordChanged(DownloadRecord r)
    {
        var was = _lastStates.GetValueOrDefault(r.Id);
        _lastStates[r.Id] = r.State;
        if (r.State != DownloadState.Completed || was == DownloadState.Completed) return;
        var name = r.FilePath != null ? Path.GetFileName(r.FilePath) : Categories.NameFromUrl(r.Request.Url);
        DownloadCompleted?.Invoke(name);
        if (_progress.Remove(r.Id, out var watching))
        {
            // The progress window gives way to "Download complete", in the same place (it may be hidden in the tray).
            Point? at = watching.IsVisible && watching.WindowState == WindowState.Normal ? new Point(watching.Left, watching.Top) : null;
            watching.Close();
            if (_settings.ShowCompleteDialog) ShowComplete(r, at);
            return;
        }
        if (!IsVisible) return; // the tray says so
        var path = r.FilePath;
        ShowBanner($"{name} is complete", false,
            ("Open", () => { if (path != null) new ExplorerShell().Open(path); }),
            ("Show in folder", () => { if (path != null) new ExplorerShell().ShowInFolder(path); }));
    }

    // ---------------------------------------------------------------- menu and toolbar

    void Open(Window w) { w.Owner = this; w.ShowDialog(); }
    void Batch_Click(object sender, RoutedEventArgs e) => Open(new BatchWindow(_vm));
    void Grabber_Click(object sender, RoutedEventArgs e) => Open(new GrabberWindow(_svc));
    void Queues_Click(object sender, RoutedEventArgs e) => Open(new QueuesWindow(_svc));
    void Logins_Click(object sender, RoutedEventArgs e) => Open(new SitesLoginWindow(_svc));
    void Proxy_Click(object sender, RoutedEventArgs e) => Open(new ProxyProfilesWindow(_svc));
    void History_Click(object sender, RoutedEventArgs e) => Open(new HistoryWindow(_svc));
    void Options_Click(object sender, RoutedEventArgs e) => Open(new OptionsWindow(_svc));
    void DropTarget_Click(object sender, RoutedEventArgs e) => ((App)Application.Current).ToggleDropTarget();
    void Exit_Click(object sender, RoutedEventArgs e) => ((App)Application.Current).ExitApp();

    void ShowProgress_Click(object sender, RoutedEventArgs e)
    {
        foreach (var i in _vm.SelectedItems) ShowProgress(i.Id, always: true);
    }

    void Theme_Click(object sender, RoutedEventArgs e)
    {
        _settings.ThemeMode = (string)((FrameworkElement)sender).Tag;
        ThemeManager.Apply(_settings.ThemeMode);
        try { _settings.Save(); } catch (IOException) { /* applies for this session */ }
    }

    void ToggleCategories_Click(object sender, RoutedEventArgs e)
    {
        var show = TreePane.Visibility != Visibility.Visible;
        if (!show) _treeWidth = TreeColumn.Width;
        TreePane.Visibility = TreeSplitter.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        TreeColumn.Width = show ? _treeWidth : new GridLength(0);
    }

    void Logs_Click(object sender, RoutedEventArgs e)
    {
        var logs = Path.Combine(AppSettings.DataDir, "logs");
        Directory.CreateDirectory(logs);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{logs}\"") { UseShellExecute = true });
    }

    void About_Click(object sender, RoutedEventArgs e)
    {
        var v = typeof(MainWindow).Assembly.GetName().Version;
        ShowBanner($"Better Download Manager {v?.ToString(3)} · personal download manager by Farzonline. DRM-protected video is never downloaded.");
    }

    // ---------------------------------------------------------------- list

    void List_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        _vm.SetSelection(List.SelectedItems.OfType<DownloadItemViewModel>());

    void List_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if ((e.OriginalSource as DependencyObject)?.FindAncestor<ListViewItem>() == null || List.SelectedItem is not DownloadItemViewModel item) return;
        if (item.HasFile) _vm.OpenFileCommand.Execute(null);
        else ShowProgress(item.Id, always: true);
    }

    /// <summary>Click a column header to sort by it; click again to reverse.</summary>
    void Header_Click(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not GridViewColumnHeader { Tag: string key }) return;
        var view = CollectionViewSource.GetDefaultView(List.ItemsSource);
        var dir = view.SortDescriptions is [{ } s] && s.PropertyName == key && s.Direction == ListSortDirection.Ascending
            ? ListSortDirection.Descending : ListSortDirection.Ascending;
        view.SortDescriptions.Clear();
        view.SortDescriptions.Add(new SortDescription(key, dir));
        foreach (var c in ((GridView)List.View).Columns)
        {
            if (c.Header is not GridViewColumnHeader { Content: string text } h) continue;
            var sorted = ReferenceEquals(h, e.OriginalSource);
            h.Content = SortLabel(text, sorted ? dir : null);
            if (sorted) h.SetResourceReference(ForegroundProperty, "Brush.AccentText"); else h.ClearValue(ForegroundProperty);
        }
    }

    /// <summary>A column header with ▲/▼ when the list is sorted by it, plain otherwise.</summary>
    public static string SortLabel(string header, ListSortDirection? dir) =>
        header.TrimEnd(' ', '▲', '▼') + dir switch { ListSortDirection.Ascending => "  ▲", ListSortDirection.Descending => "  ▼", _ => "" };

    void CopyAddress_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedItems.Count > 0) Clipboard.SetText(string.Join(Environment.NewLine, _vm.SelectedItems.Select(i => i.Url)));
    }

    // ---------------------------------------------------------------- keyboard + window chrome

    void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var ctrl = Keyboard.Modifiers == ModifierKeys.Control;
        if (ctrl && e.Key == Key.N) { _vm.AddUrlCommand.Execute(null); e.Handled = true; }
        else if (ctrl && e.Key == Key.F) { SearchBox.Focus(); SearchBox.SelectAll(); e.Handled = true; }
        else if (e.Key == Key.Delete && List.IsKeyboardFocusWithin) { _vm.RemoveCommand.Execute(null); e.Handled = true; }
        else if (e.Key == Key.Escape && SearchBox.IsKeyboardFocused) { _vm.SearchText = ""; List.Focus(); e.Handled = true; }
    }

    void Min_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
    void Max_Click(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this); else SystemCommands.MaximizeWindow(this);
    }
    void Close_Click(object sender, RoutedEventArgs e) => Close();

    void Window_StateChanged(object? sender, EventArgs e)
    {
        var max = WindowState == WindowState.Maximized;
        MaxButton.Content = max ? "" : "";
        MaxButton.ToolTip = max ? "Restore" : "Maximize";
        // A maximized chrome window overhangs the work area by its resize border: pull it back in.
        Root.Margin = max ? SystemParameters.WindowResizeBorderThickness : new Thickness(0);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_settings.MinimizeToTray && !App.IsExiting) { e.Cancel = true; Hide(); return; }
        base.OnClosing(e);
    }
}

static class VisualTreeExtensions
{
    public static T? FindAncestor<T>(this DependencyObject d) where T : DependencyObject
    {
        for (var p = d; p != null; p = VisualTreeHelper.GetParent(p) ?? LogicalTreeHelper.GetParent(p)) if (p is T t) return t;
        return null;
    }
}
