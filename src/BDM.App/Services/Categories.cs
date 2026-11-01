using BDM.App.Settings;

namespace BDM.App.Services;

public static class Categories
{
    public const string All = "All Downloads", Unfinished = "Unfinished", Finished = "Finished";

    static readonly Dictionary<string, string[]> ByType = new()
    {
        ["Compressed"] = ["zip", "rar", "7z", "gz", "tgz", "bz2", "xz", "tar", "iso", "cab"],
        ["Documents"] = ["pdf", "doc", "docx", "xls", "xlsx", "ppt", "pptx", "txt", "epub", "rtf", "csv"],
        ["Music"] = ["mp3", "flac", "wav", "aac", "m4a", "ogg", "opus", "wma"],
        ["Programs"] = ["exe", "msi", "msix", "appx", "apk", "deb", "rpm", "dmg", "jar", "bat"],
        ["Video"] = ["mp4", "mkv", "avi", "mov", "wmv", "flv", "webm", "m4v", "mpg", "mpeg", "ts", "m3u8", "mpd"],
    };

    /// <summary>Sidebar order: views first, then file types.</summary>
    public static IReadOnlyList<string> Sidebar { get; } = [All, Unfinished, Finished, "Compressed", "Documents", "Music", "Programs", "Video", "Other"];

    /// <summary>Segoe Fluent Icons code point for a sidebar entry or file type.</summary>
    public static string Glyph(string category) => category switch
    {
        All => "",
        Unfinished => "",
        Finished => "",
        "Compressed" => "",
        "Documents" => "",
        "Music" => "",
        "Programs" => "",
        "Video" => "",
        _ => "",
    };

    /// <summary>Name of the colour (Hue.* in Themes/Brand.xaml) that tints a category's icon tile.</summary>
    public static string Hue(string category) => category switch
    {
        All => "Indigo",
        Unfinished => "Amber",
        Finished => "Emerald",
        "Compressed" => "Orange",
        "Documents" => "Sky",
        "Music" => "Pink",
        "Programs" => "Violet",
        "Video" => "Rose",
        _ => "Slate",
    };

    public static string For(string fileName)
    {
        var ext = Path.GetExtension(fileName).TrimStart('.').ToLowerInvariant();
        return ByType.FirstOrDefault(kv => kv.Value.Contains(ext)).Key ?? "Other";
    }

    public static string FolderFor(AppSettings s, string category)
    {
        var key = ByType.ContainsKey(category) ? category : "Other";
        return s.CategoryFolders.TryGetValue(key, out var custom) && !string.IsNullOrWhiteSpace(custom) ? custom : Path.Combine(s.DownloadRoot, key);
    }

    public static string NameFromUrl(Uri url)
    {
        var name = Uri.UnescapeDataString(Path.GetFileName(url.AbsolutePath));
        return string.IsNullOrWhiteSpace(name) ? "download" : name;
    }
}
