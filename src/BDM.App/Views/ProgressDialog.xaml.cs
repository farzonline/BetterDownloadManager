using System.Windows;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Shell;
using System.Windows.Threading;
using BDM.App.Services;
using BDM.App.ViewModels;
using BDM.Core.Data;
using BDM.Core.Engine;

namespace BDM.App.Views;

/// <summary>
/// One download's own window (IDM opens one per download): live numbers 4 times a second, its own taskbar button
/// with progress, and Open / Show in folder once the file is there. Closing it never stops the download.
/// </summary>
public partial class ProgressDialog : Window
{
    readonly DownloadManager _manager;
    readonly Guid _id;
    readonly DownloadItemViewModel _vm;
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };

    public Guid DownloadId => _id;

    public ProgressDialog(DownloadManager manager, Guid id)
    {
        InitializeComponent();
        ThemeManager.Attach(this);
        Icon = AppIcon.Image();
        _manager = manager;
        _id = id;
        var rec = manager.Get(id) ?? throw new InvalidOperationException("That download no longer exists.");
        _vm = new DownloadItemViewModel(rec, DateTimeOffset.UtcNow);
        DataContext = _vm;
        _timer.Tick += (_, _) => Refresh();
        Loaded += (_, _) => { Refresh(); _timer.Start(); };
        Closed += (_, _) => _timer.Stop();
    }

    void Refresh()
    {
        var rec = _manager.Get(_id);
        if (rec == null) { Close(); return; }
        _vm.Update(rec, DateTimeOffset.UtcNow);
        var done = rec.State == DownloadState.Completed;
        var segs = _manager.SegmentsFor(_id);

        Title = done ? $"Done · {_vm.Name}" : _vm.Progress >= 0 ? $"{_vm.PercentText} · {_vm.Name}" : _vm.Name;
        SizeValue.Text = rec.Total is > 0 ? Format.Size(rec.Total.Value) : "Unknown";
        DownloadedValue.Text = rec.Total is > 0 ? $"{Format.Size(rec.Downloaded)}  ({_vm.PercentText})" : Format.Size(rec.Downloaded);
        ConnectionsValue.Text = segs is { Count: > 0 } ? $"{segs.Count(s => s.Downloaded < s.End - s.Start + 1)} active of {segs.Count}" : rec.Stream != null ? "Stream (one part at a time)" : "—";
        var path = rec.FilePath ?? System.IO.Path.Combine(rec.Request.DestinationDir, rec.Request.FileName ?? _vm.Name);
        SaveTo.Text = path;
        SaveTo.ToolTip = path;

        PauseResume.Content = _vm.CanResume ? "Resume" : "Pause";
        Ico.SetGlyph(PauseResume, _vm.CanResume ? "" : "");
        PauseResume.IsEnabled = _vm.CanResume || _vm.CanPause;
        PauseResume.Visibility = done ? Visibility.Collapsed : Visibility.Visible;
        OpenButton.Visibility = FolderButton.Visibility = Done.Visibility = done ? Visibility.Visible : Visibility.Collapsed;
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

    void PauseResume_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.CanResume) _manager.Resume(_id); else if (_vm.CanPause) _manager.Pause(_id);
    }

    void Open_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.FilePath != null) new ExplorerShell().Open(_vm.FilePath);
        Close();
    }

    void Folder_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.FilePath != null) new ExplorerShell().ShowInFolder(_vm.FilePath);
    }
}
