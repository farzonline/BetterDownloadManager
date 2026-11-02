using System.Text.Json;

namespace BDM.App.Settings;

/// <summary>User settings, stored as JSON in %AppData%\BetterDownloadManager\settings.json.</summary>
public class AppSettings
{
    /// <summary>%AppData%\BetterDownloadManager, or the folder named by BDM_DATA_DIR (portable use and tests).</summary>
    public static string DataDir { get; } = Environment.GetEnvironmentVariable("BDM_DATA_DIR")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BetterDownloadManager");
    public static string DefaultPath => Path.Combine(DataDir, "settings.json");

    public string DownloadRoot { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "BDM");
    public bool AlwaysAskQuality { get; set; }
    /// <summary>System, Light or Dark.</summary>
    public string ThemeMode { get; set; } = "System";
    public string? FfmpegPath { get; set; }
    public string? YtDlpPath { get; set; }
    public bool MinimizeToTray { get; set; } = true;
    /// <summary>Copying a download link opens the download dialog.</summary>
    public bool WatchClipboard { get; set; } = true;
    /// <summary>Add URL starts with the copied link filled in.</summary>
    public bool PasteLinkOnAdd { get; set; } = true;
    /// <summary>Ask for name, folder and description before each download from the browser (IDM's "Download File Info").</summary>
    public bool ShowDownloadDialog { get; set; } = true;
    /// <summary>Open a progress window for each download started from the download dialog.</summary>
    public bool ShowProgressWindow { get; set; } = true;
    /// <summary>When a download with its own progress window finishes, show IDM's "Download complete" window.</summary>
    public bool ShowCompleteDialog { get; set; } = true;
    public bool StartWithWindows { get; set; }
    /// <summary>Lets the `bdm` command-line tool and scripts control the app through a token-protected loopback API (needs a restart).</summary>
    public bool EnableLocalApi { get; set; } = true;
    /// <summary>Connections per download for the main queue (1-32).</summary>
    public int Connections { get; set; } = 8;
    /// <summary>Total download speed limit in KB/s; 0 = unlimited.</summary>
    public int SpeedLimitKBps { get; set; }
    /// <summary>Scanner run on each finished file; {file} is replaced by its path (appended when absent).</summary>
    public string? AntivirusCommand { get; set; }
    public string? GlobalProxyId { get; set; }
    public Dictionary<string, string> CategoryProxies { get; set; } = new();
    /// <summary>Folder overrides per category; otherwise DownloadRoot\Category.</summary>
    public Dictionary<string, string> CategoryFolders { get; set; } = new();
    public List<string> ClipboardTypes { get; set; } = new()
    {
        "zip", "rar", "7z", "gz", "tgz", "bz2", "xz", "tar", "iso", "exe", "msi", "msix", "apk", "dmg", "mp4", "mkv", "avi", "mov",
        "wmv", "flv", "webm", "m4v", "mp3", "flac", "wav", "m4a", "pdf", "epub", "docx", "xlsx", "pptx", "m3u8", "mpd",
    };

    /// <summary>Copies every setting from another instance (used by import and by the options window).</summary>
    public void CopyFrom(AppSettings other)
    {
        foreach (var p in typeof(AppSettings).GetProperties().Where(p => p.CanRead && p.CanWrite))
            p.SetValue(this, p.GetValue(other));
    }

    public static AppSettings Load(string? path = null)
    {
        try
        {
            path ??= DefaultPath;
            return File.Exists(path) ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path)) ?? new() : new();
        }
        catch (Exception e) when (e is JsonException or IOException) { return new(); }
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
