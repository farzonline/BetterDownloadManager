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
    public bool WatchClipboard { get; set; } = true;
    public List<string> ClipboardTypes { get; set; } = new()
    {
        "zip", "rar", "7z", "gz", "tgz", "bz2", "xz", "tar", "iso", "exe", "msi", "msix", "apk", "dmg", "mp4", "mkv", "avi", "mov",
        "wmv", "flv", "webm", "m4v", "mp3", "flac", "wav", "m4a", "pdf", "epub", "docx", "xlsx", "pptx", "m3u8", "mpd",
    };

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
