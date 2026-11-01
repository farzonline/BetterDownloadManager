using System.Windows;
using System.Windows.Controls;
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
        Crest.Source = AppIcon.Image();
        Icon = AppIcon.Image();
        ThemeManager.Attach(this);

        foreach (var r in manager.All()) _lastStates[r.Id] = r.State;
        manager.Changed += r => Dispatcher.BeginInvoke(() => OnRecordChanged(r));
        vm.AddUrlRequested += () => ShowAddDialog(null);
        vm.RemoveRequested += ConfirmRemove;
    }

    // ---------------------------------------------------------------- add / remove

    public async void ShowAddDialog(string? initialUrl)
    {
        if (!IsVisible) { Show(); }
        var dlg = new AddUrlDialog(_svc.Clients.Get(null), _settings, initialUrl, _svc.Clients.Choices("Use the default")) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        try
        {
            if (dlg.UseYtDlp)
            {
                var reply = await _svc.Handler.HandleAsync(new BDM.Core.Ipc.HostMessage("addPageAsk", dlg.Url, null, null, null, null, null, null));
                if (!reply.Ok) ShowBanner(reply.Error ?? "yt-dlp couldn't use that page.", error: true);
                return;
            }
            var id = _vm.AddUrl(dlg.Url, dlg.ProxyProfileId);
            if (!dlg.StartNow) _manager.Pause(id);
        }
        catch (ArgumentException e) { ShowBanner(e.Message, error: true); }
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

    /// <summary>A link on the clipboard that looks like a file: offer it without stealing focus.</summary>
    public void OfferClipboardUrl(Uri url) =>
        ShowBanner($"Link copied: {Categories.NameFromUrl(url)}", error: false, ("Download", () => ShowAddDialog(url.ToString())));

    void OnRecordChanged(DownloadRecord r)
    {
        var was = _lastStates.GetValueOrDefault(r.Id);
        _lastStates[r.Id] = r.State;
        if (r.State != DownloadState.Completed || was == DownloadState.Completed) return;
        var name = r.FilePath != null ? Path.GetFileName(r.FilePath) : Categories.NameFromUrl(r.Request.Url);
        DownloadCompleted?.Invoke(name);
        if (!IsVisible) return;
        var path = r.FilePath;
        ShowBanner($"{name} is complete", false,
            ("Open", () => { if (path != null) new ExplorerShell().Open(path); }),
            ("Show in folder", () => { if (path != null) new ExplorerShell().ShowInFolder(path); }));
    }

    // ---------------------------------------------------------------- more menu

    void More_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = MoreButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        void Item(string glyph, string header, Action act)
        {
            var icon = new TextBlock { Text = glyph, Style = (Style)FindResource("Text.MenuIcon") };
            var mi = new MenuItem { Header = header, Icon = icon };
            mi.Click += (_, _) => act();
            menu.Items.Add(mi);
        }
        Item("", "Batch download…", () => new BatchWindow(_vm) { Owner = this }.ShowDialog());
        Item("", "Site grabber…", () => new GrabberWindow(_svc) { Owner = this }.ShowDialog());
        menu.Items.Add(new Separator());
        Item("", "Queues and schedule…", () => new QueuesWindow(_svc) { Owner = this }.ShowDialog());
        Item("", "Site logins…", () => new SitesLoginWindow(_svc) { Owner = this }.ShowDialog());
        Item("", "Proxy and VPN profiles…", () => new ProxyProfilesWindow(_svc) { Owner = this }.ShowDialog());
        Item("", "Download history…", () => new HistoryWindow(_svc) { Owner = this }.ShowDialog());
        menu.Items.Add(new Separator());
        Item("", "Show or hide drop target", () => ((App)Application.Current).ToggleDropTarget());
        Item("", "Options…", () => new OptionsWindow(_svc) { Owner = this }.ShowDialog());
        menu.IsOpen = true;
    }

    // ---------------------------------------------------------------- list

    void List_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        _vm.SetSelection(List.SelectedItems.OfType<DownloadItemViewModel>());

    void List_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if ((e.OriginalSource as DependencyObject)?.FindAncestor<ListViewItem>() == null || List.SelectedItem is not DownloadItemViewModel item) return;
        if (item.HasFile) _vm.OpenFileCommand.Execute(null);
        else new ProgressDialog(_manager, item.Id) { Owner = this }.Show();
    }

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

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
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
