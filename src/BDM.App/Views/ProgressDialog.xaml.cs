using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Shell;
using System.Windows.Threading;
using BDM.App.Services;
using BDM.App.Settings;
using BDM.App.ViewModels;
using BDM.Core.Data;
using BDM.Core.Engine;

namespace BDM.App.Views;

/// <summary>IDM's "Options on completion": what happens once this one download is done.</summary>
public enum WhenDone { Nothing, ExitApp, Sleep, Hibernate, Shutdown }

/// <summary>
/// One download's own window (IDM opens one per download): live numbers 4 times a second, a row per connection, its own
/// taskbar button with progress, and "To tray" to tuck it away. Closing it never stops the download; when the download
/// finishes the main window swaps it for the "download complete" window.
/// </summary>
public partial class ProgressDialog : Window
{
    readonly DownloadManager _manager;
    readonly Guid _id;
    readonly AppSettings _settings;
    readonly DownloadItemViewModel _vm;
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    readonly SegmentRows _rows = new();
    readonly ObservableCollection<SegmentRow> _parts = new();
    System.Windows.Forms.NotifyIcon? _tray;

    public Guid DownloadId => _id;
    public bool ShowCompleteWindow => ShowCompleteBox.IsChecked == true;
    public WhenDone WhenDone => (WhenDone)Math.Max(0, WhenDoneBox.SelectedIndex);

    public ProgressDialog(DownloadManager manager, Guid id, AppSettings settings)
    {
        InitializeComponent();
        ThemeManager.Attach(this);
        Icon = AppIcon.Image();
        _manager = manager;
        _id = id;
        _settings = settings;
        var rec = manager.Get(id) ?? throw new InvalidOperationException("That download no longer exists.");
        _vm = new DownloadItemViewModel(rec, DateTimeOffset.UtcNow);
        DataContext = _vm;
        Parts.ItemsSource = _parts;
        ShowCompleteBox.IsChecked = settings.ShowCompleteDialog;
        ShowDetails(settings.ShowProgressDetails);
        // Once the owner drags the window bigger, the connections table takes the extra room.
        SizeChanged += (_, _) => { if (SizeToContent == SizeToContent.Manual && !double.IsNaN(Parts.Height)) Parts.Height = double.NaN; };
        if (rec.Request.SpeedLimitBytesPerSec is { } limit) { LimitKb.Text = (limit / 1024).ToString(); LimitBox.IsChecked = true; }
        if (rec.Stream != null || rec.YtDlpFormat != null || rec.Request.Url.Scheme is "ftp" or "ftps")
        {
            LimitBox.IsEnabled = LimitKb.IsEnabled = false;
            LimitNote.Text = "The speed limiter works on file downloads; streams, video pages and FTP don't use it.";
        }
        _timer.Tick += (_, _) => Refresh();
        Loaded += (_, _) => { Refresh(); _timer.Start(); };
        Closed += (_, _) => { _timer.Stop(); _tray?.Dispose(); };
    }

    void Refresh()
    {
        var rec = _manager.Get(_id);
        if (rec == null) { Close(); return; }
        _vm.Update(rec, DateTimeOffset.UtcNow);
        var segs = _manager.SegmentsFor(_id);

        Title = _vm.Progress >= 0 ? $"{_vm.PercentText} · {_vm.Name}" : _vm.Name;
        if (_tray != null) _tray.Text = Title.Length > 63 ? Title[..62] + "…" : Title; // the tray's limit
        SizeValue.Text = rec.Total is > 0 ? Format.Size(rec.Total.Value) : "Unknown";
        DownloadedValue.Text = rec.Total is > 0 ? $"{Format.Size(rec.Downloaded)}  ({_vm.PercentText})" : Format.Size(rec.Downloaded);
        ConnectionsValue.Text = segs is { Count: > 0 } ? $"{segs.Count(s => s.Remaining > 0)} active of {segs.Count}" : rec.Stream != null ? "Stream (one part at a time)" : "—";
        var path = rec.FilePath ?? System.IO.Path.Combine(rec.Request.DestinationDir, rec.Request.FileName ?? _vm.Name);
        SaveTo.Text = path;
        SaveTo.ToolTip = path;

        PauseResume.Content = _vm.CanResume ? "Resume" : "Pause";
        Ico.SetGlyph(PauseResume, _vm.CanResume ? "" : "");
        PauseResume.IsEnabled = _vm.CanResume || _vm.CanPause;
        Bar.IsIndeterminate = _vm.Progress < 0;

        Taskbar.ProgressState = rec.State switch
        {
            DownloadState.Running => _vm.Progress < 0 ? TaskbarItemProgressState.Indeterminate : TaskbarItemProgressState.Normal,
            DownloadState.Paused or DownloadState.Queued => TaskbarItemProgressState.Paused,
            DownloadState.Failed => TaskbarItemProgressState.Error,
            _ => TaskbarItemProgressState.None,
        };
        Taskbar.ProgressValue = Math.Max(0, _vm.Progress) / 100;
        DrawSegments(rec.Total, segs);
        ShowParts(segs, rec.State == DownloadState.Running);
    }

    /// <summary>Updates the rows in place, so the table keeps its scroll position.</summary>
    void ShowParts(IReadOnlyList<Segment>? segs, bool running)
    {
        PartsCard.Visibility = segs is { Count: > 0 } ? Visibility.Visible : Visibility.Collapsed;
        if (segs is not { Count: > 0 }) return;
        var rows = _rows.Update(segs, running, DateTimeOffset.UtcNow);
        for (var i = 0; i < rows.Count; i++)
            if (i >= _parts.Count) _parts.Add(rows[i]); else if (_parts[i] != rows[i]) _parts[i] = rows[i];
        while (_parts.Count > rows.Count) _parts.RemoveAt(_parts.Count - 1);
    }

    void DrawSegments(long? total, IReadOnlyList<Segment>? segs)
    {
        Segments.Children.Clear();
        if (segs == null || total is not > 0 || Segments.ActualWidth <= 0) return;
        var track = (Brush)FindResource("Brush.Raised");
        var fill = (Brush)FindResource("Brush.Focus");
        foreach (var s in segs)
        {
            var len = s.End - s.Start + 1;
            var x = s.Start * Segments.ActualWidth / total.Value;
            var w = Math.Max(2, len * Segments.ActualWidth / total.Value - 2);
            var got = Math.Min(len, Math.Max(0, s.Downloaded));
            Segments.Children.Add(new Rectangle { Width = w, Height = 10, RadiusX = 3, RadiusY = 3, Fill = track, Margin = new Thickness(x, 0, 0, 0) });
            if (got > 0)
                Segments.Children.Add(new Rectangle { Width = Math.Max(2, w * got / len), Height = 10, RadiusX = 3, RadiusY = 3, Fill = fill, Margin = new Thickness(x, 0, 0, 0) });
        }
    }

    /// <summary>IDM's per-download "minimize to tray": the window hides behind its own notification-area icon.</summary>
    public void ToTray()
    {
        if (_tray == null)
        {
            _tray = new System.Windows.Forms.NotifyIcon { Icon = AppIcon.TrayIcon(), Text = "Better Download Manager" };
            _tray.MouseClick += (_, e) => { if (e.Button == System.Windows.Forms.MouseButtons.Left) BringBack(); };
            _tray.ContextMenuStrip = new System.Windows.Forms.ContextMenuStrip();
            _tray.ContextMenuStrip.Items.Add("Show progress", null, (_, _) => BringBack());
        }
        _tray.Visible = true;
        Hide();
    }

    /// <summary>Back from the tray (or minimized) to the front.</summary>
    public void BringBack()
    {
        if (_tray != null) _tray.Visible = false;
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (StatusPage == null) return; // while the window is still being built
        StatusPage.Visibility = StatusTab.IsChecked == true ? Visibility.Visible : Visibility.Hidden;
        SpeedPage.Visibility = SpeedTab.IsChecked == true ? Visibility.Visible : Visibility.Hidden;
        FinishPage.Visibility = FinishTab.IsChecked == true ? Visibility.Visible : Visibility.Hidden;
    }

    /// <summary>Applies the speed limiter at once (ticking the box, leaving the number box, or Enter).</summary>
    void Limit_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        long? limit = LimitBox.IsChecked == true && long.TryParse(LimitKb.Text.Trim(), out var kb) && kb > 0 ? kb * 1024 : null;
        var on = limit != null;
        _manager.SetSpeedLimit(_id, limit);
        if (LimitBox.IsChecked == true && !on) LimitNote.Text = "Enter a speed in KB/s (a whole number above 0) to turn the limiter on.";
    }

    void LimitKb_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter) { Limit_Changed(sender, e); e.Handled = true; }
    }

    /// <summary>IDM's "Hide details": just the bars and the buttons, or everything. Remembered for the next window.</summary>
    void ShowDetails(bool on)
    {
        Details.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        DetailsButton.Content = on ? "Hide details" : "Show details";
        Ico.SetGlyph(DetailsButton, on ? "" : "");
        Parts.Height = 150;
        SizeToContent = SizeToContent.Height; // fit the new content, then the owner may resize again
    }

    void Details_Click(object sender, RoutedEventArgs e)
    {
        _settings.ShowProgressDetails = Details.Visibility != Visibility.Visible;
        try { _settings.Save(); } catch (IOException) { /* still applies to this window */ }
        ShowDetails(_settings.ShowProgressDetails);
    }

    void Tray_Click(object sender, RoutedEventArgs e) => ToTray();
    void Close_Click(object sender, RoutedEventArgs e) => Close();

    void PauseResume_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.CanResume) _manager.Resume(_id); else if (_vm.CanPause) _manager.Pause(_id);
    }
}
