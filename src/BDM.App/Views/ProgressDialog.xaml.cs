using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using BDM.App.Services;
using BDM.App.ViewModels;
using BDM.Core.Engine;

namespace BDM.App.Views;

/// <summary>Live view of one download, refreshed 4 times a second. Closing it never stops the download.</summary>
public partial class ProgressDialog : Window
{
    readonly DownloadManager _manager;
    readonly Guid _id;
    readonly DownloadItemViewModel _vm;
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };

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
        SaveTo.Text = rec.Request.DestinationDir;
        SaveTo.ToolTip = rec.Request.DestinationDir;
        _timer.Tick += (_, _) => Refresh();
        Loaded += (_, _) => { Refresh(); _timer.Start(); };
        Closed += (_, _) => _timer.Stop();
    }

    void Refresh()
    {
        var rec = _manager.Get(_id);
        if (rec == null) { Close(); return; }
        _vm.Update(rec, DateTimeOffset.UtcNow);
        PauseResume.Content = _vm.CanResume ? "Resume" : "Pause";
        PauseResume.IsEnabled = _vm.CanResume || _vm.CanPause;
        Title = _vm.Name;
        DrawSegments(rec.Total);
    }

    void DrawSegments(long? total)
    {
        Segments.Children.Clear();
        var segs = _manager.SegmentsFor(_id);
        if (segs == null || total is not > 0 || Segments.ActualWidth <= 0) return;
        var track = (Brush)FindResource("Brush.Raised");
        var fill = (Brush)FindResource("Brush.Focus");
        foreach (var s in segs)
        {
            var len = s.End - s.Start + 1;
            var x = s.Start * Segments.ActualWidth / total.Value;
            var w = Math.Max(2, len * Segments.ActualWidth / total.Value - 2);
            var done = Math.Min(len, Math.Max(0, s.Downloaded));
            Segments.Children.Add(new Rectangle { Width = w, Height = 10, RadiusX = 3, RadiusY = 3, Fill = track, Margin = new Thickness(x, 0, 0, 0) });
            if (done > 0)
                Segments.Children.Add(new Rectangle { Width = Math.Max(2, w * done / len), Height = 10, RadiusX = 3, RadiusY = 3, Fill = fill, Margin = new Thickness(x, 0, 0, 0) });
        }
    }

    void PauseResume_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.CanResume) _manager.Resume(_id); else if (_vm.CanPause) _manager.Pause(_id);
    }
}
