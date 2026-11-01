using System.Text.Json;
using Microsoft.Win32;

namespace BDM.Host;

public static class HostRegistrar
{
    public const string HostName = "com.bdm.host";

    // Chrome's key also covers Opera and Vivaldi, which read it.
    static readonly Dictionary<string, string> Browsers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["chrome"] = @"Google\Chrome",
        ["edge"] = @"Microsoft\Edge",
        ["brave"] = @"BraveSoftware\Brave-Browser",
        ["chromium"] = "Chromium",
    };

    /// <summary>Writes the host manifest next to the exe and points each browser's per-user registry key at it (no admin needed).</summary>
    public static void Register(string hostExe, string extensionId, string[] browsers, string registryRoot = "Software")
    {
        var manifestPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(hostExe))!, HostName + ".json");
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(new
        {
            name = HostName,
            description = "Better Download Manager native messaging host",
            path = hostExe,
            type = "stdio",
            allowed_origins = new[] { $"chrome-extension://{extensionId}/" },
        }, new JsonSerializerOptions { WriteIndented = true }));

        foreach (var b in browsers)
        {
            if (!Browsers.TryGetValue(b, out var sub)) throw new ArgumentException($"Unknown browser '{b}'.");
            using var key = Registry.CurrentUser.CreateSubKey($@"{registryRoot}\{sub}\NativeMessagingHosts\{HostName}");
            key.SetValue(null, manifestPath);
        }
    }

    public static void Unregister(string[] browsers, string registryRoot = "Software")
    {
        foreach (var b in browsers)
            if (Browsers.TryGetValue(b, out var sub))
                Registry.CurrentUser.DeleteSubKeyTree($@"{registryRoot}\{sub}\NativeMessagingHosts\{HostName}", throwOnMissingSubKey: false);
    }
}
