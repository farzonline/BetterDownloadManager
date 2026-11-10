using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace BDM.App.Services;

/// <summary>
/// The small icon Windows shows for a file type, i.e. the icon of the app that opens it (as IDM's list does). Looked up by
/// extension only, so it works before the file exists; one lookup per type.
/// </summary>
public static class FileIcons
{
    static readonly Dictionary<string, BitmapSource> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static BitmapSource For(string fileName)
    {
        var ext = Path.GetExtension(fileName);
        if (ext.Length == 0) ext = ".";
        lock (Cache)
        {
            if (Cache.TryGetValue(ext, out var hit)) return hit;
            var info = new SHFILEINFO();
            // USEFILEATTRIBUTES: describe the type without touching the disk.
            SHGetFileInfo("file" + ext, 0x80 /* FILE_ATTRIBUTE_NORMAL */, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), 0x100 | 0x1 | 0x10 /* ICON | SMALLICON | USEFILEATTRIBUTES */);
            BitmapSource img;
            try
            {
                img = info.hIcon != IntPtr.Zero
                    ? Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(16, 16))
                    : BitmapSource.Create(16, 16, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, new byte[16 * 16 * 4], 16 * 4);
            }
            finally { if (info.hIcon != IntPtr.Zero) DestroyIcon(info.hIcon); }
            img.Freeze();
            return Cache[ext] = img;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr SHGetFileInfo(string path, uint attributes, ref SHFILEINFO info, uint size, uint flags);

    [DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr icon);
}
