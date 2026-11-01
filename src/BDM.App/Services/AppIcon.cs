using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace BDM.App.Services;

/// <summary>The app's crest, drawn in code (logo green with a download arrow) so no binary asset is needed.</summary>
public static class AppIcon
{
    static Bitmap Draw(int size)
    {
        var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);
        var r = size * 0.22f;
        using var path = new GraphicsPath();
        var rect = new RectangleF(0, 0, size - 1, size - 1);
        path.AddArc(rect.X, rect.Y, r * 2, r * 2, 180, 90);
        path.AddArc(rect.Right - r * 2, rect.Y, r * 2, r * 2, 270, 90);
        path.AddArc(rect.Right - r * 2, rect.Bottom - r * 2, r * 2, r * 2, 0, 90);
        path.AddArc(rect.X, rect.Bottom - r * 2, r * 2, r * 2, 90, 90);
        path.CloseFigure();
        using (var fill = new SolidBrush(Color.FromArgb(0x04, 0xfa, 0x73))) g.FillPath(fill, path);
        using var ink = new SolidBrush(Color.FromArgb(0x02, 0x14, 0x0a));
        float s = size;
        g.FillRectangle(ink, s * 0.43f, s * 0.2f, s * 0.14f, s * 0.34f);
        g.FillPolygon(ink, new[] { new PointF(s * 0.27f, s * 0.5f), new PointF(s * 0.73f, s * 0.5f), new PointF(s * 0.5f, s * 0.74f) });
        g.FillRectangle(ink, s * 0.25f, s * 0.78f, s * 0.5f, s * 0.07f);
        return bmp;
    }

    public static System.Drawing.Icon TrayIcon()
    {
        using var bmp = Draw(32);
        return System.Drawing.Icon.FromHandle(bmp.GetHicon());
    }

    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr hObject);

    public static BitmapSource Image(int size = 64)
    {
        using var bmp = Draw(size);
        var hbmp = bmp.GetHbitmap(Color.Transparent);
        try
        {
            var src = Imaging.CreateBitmapSourceFromHBitmap(hbmp, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        finally { DeleteObject(hbmp); }
    }
}
