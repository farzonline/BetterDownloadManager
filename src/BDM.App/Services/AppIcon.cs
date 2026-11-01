using System.Windows.Media.Imaging;

namespace BDM.App.Services;

/// <summary>The brand mark (Assets/bdm.ico, drawn by tools/make-icon.py), for windows and the tray.</summary>
public static class AppIcon
{
    static Stream Open() => typeof(AppIcon).Assembly.GetManifestResourceStream("bdm.ico")!;

    public static System.Drawing.Icon TrayIcon()
    {
        using var s = Open();
        return new System.Drawing.Icon(s, 32, 32);
    }

    /// <summary>The icon frame closest to <paramref name="size"/> pixels (rounding up).</summary>
    public static BitmapSource Image(int size = 64)
    {
        using var s = Open();
        var frames = new IconBitmapDecoder(s, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames;
        var frame = frames.Where(f => f.PixelWidth >= size).MinBy(f => f.PixelWidth) ?? frames.MaxBy(f => f.PixelWidth)!;
        frame.Freeze();
        return frame;
    }
}
