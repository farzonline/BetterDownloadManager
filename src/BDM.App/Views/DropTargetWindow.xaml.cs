using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace BDM.App.Views;

/// <summary>Small always-on-top square: drop a link (or a selection of text with links) on it to download.</summary>
public partial class DropTargetWindow : Window
{
    readonly Action<string> _addUrl;

    public DropTargetWindow(Action<string> addUrl)
    {
        InitializeComponent();
        _addUrl = addUrl;
        var area = SystemParameters.WorkArea;
        Left = area.Right - Width - 24;
        Top = area.Bottom - Height - 24;
    }

    void Window_DragEnter(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.UnicodeText) ? DragDropEffects.Copy : DragDropEffects.None;
        Box.BorderBrush = (Brush)FindResource("Brush.Focus");
        e.Handled = true;
    }

    void Window_DragLeave(object sender, DragEventArgs e) => Box.BorderBrush = (Brush)FindResource("Brush.Line");

    void Window_Drop(object sender, DragEventArgs e)
    {
        Box.BorderBrush = (Brush)FindResource("Brush.Line");
        if (e.Data.GetData(DataFormats.UnicodeText) is not string text) return;
        foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (Uri.TryCreate(line, UriKind.Absolute, out var u) && u.Scheme is "http" or "https" or "ftp" or "ftps") _addUrl(line);
    }

    void Window_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) { Hide(); return; }
        DragMove();
    }
}
