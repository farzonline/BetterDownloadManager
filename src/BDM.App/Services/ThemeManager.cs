using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace BDM.App.Services;

/// <summary>Voltage (dark) / Daylight (light) from the theme-studio tokens; "System" follows the Windows app mode live.</summary>
public static class ThemeManager
{
    const string Base = "pack://application:,,,/BDM.App;component/Themes/";
    static string _mode = "System";

    public static bool IsDark { get; private set; }
    public static event Action? Changed;

    public static void Initialize(string mode)
    {
        SystemEvents.UserPreferenceChanged += (_, _) =>
        {
            if (_mode == "System") Application.Current.Dispatcher.BeginInvoke(() => Apply(_mode));
        };
        Apply(mode);
    }

    public static void Apply(string mode)
    {
        _mode = mode;
        var dark = mode == "Dark" || (mode != "Light" && SystemPrefersDark());
        var merged = Application.Current.Resources.MergedDictionaries;
        var theme = new ResourceDictionary { Source = new Uri(Base + (dark ? "Voltage.xaml" : "Daylight.xaml")) };
        if (merged.Count == 0)
        {
            merged.Add(theme);
            merged.Add(new ResourceDictionary { Source = new Uri(Base + "Controls.xaml") });
        }
        else merged[0] = theme;
        IsDark = dark;
        foreach (Window w in Application.Current.Windows) ApplyChrome(w);
        Changed?.Invoke();
    }

    static bool SystemPrefersDark()
    {
        try { return Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) is 0; }
        catch (Exception) { return true; }
    }

    /// <summary>Dark/light title bar and a border/caption in the token colours (Windows 11; harmless elsewhere).</summary>
    public static void Attach(Window w)
    {
        w.SourceInitialized += (_, _) => ApplyChrome(w);
        Changed += () => ApplyChrome(w);
    }

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    static int ColorRef(string key)
    {
        var c = (Color)Application.Current.FindResource(key);
        return c.R | (c.G << 8) | (c.B << 16);
    }

    static void ApplyChrome(Window w)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        if (hwnd == IntPtr.Zero) return;
        var dark = IsDark ? 1 : 0;
        DwmSetWindowAttribute(hwnd, 20, ref dark, 4);            // DWMWA_USE_IMMERSIVE_DARK_MODE
        var border = ColorRef("Color.Line");
        DwmSetWindowAttribute(hwnd, 34, ref border, 4);          // DWMWA_BORDER_COLOR
        var caption = ColorRef("Color.Background");
        DwmSetWindowAttribute(hwnd, 35, ref caption, 4);         // DWMWA_CAPTION_COLOR
        var text = ColorRef("Color.Text");
        DwmSetWindowAttribute(hwnd, 36, ref text, 4);            // DWMWA_TEXT_COLOR
    }
}
