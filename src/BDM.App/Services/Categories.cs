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

    /// <summary>Category tree order, as in IDM: All Downloads with the file types under it, then Unfinished and Finished.</summary>
    public static IReadOnlyList<string> Sidebar { get; } = [All, "Compressed", "Documents", "Music", "Programs", "Video", "Other", Unfinished, Finished];

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

    /// <summary>File types in the order the download dialog offers them.</summary>
    public static IReadOnlyList<string> Types { get; } = ["Compressed", "Documents", "Music", "Programs", "Video", "Other"];

    static string Key(string category) => ByType.ContainsKey(category) ? category : "Other";

    public static string FolderFor(AppSettings s, string category) =>
        s.CategoryFolders.TryGetValue(Key(category), out var custom) && !string.IsNullOrWhiteSpace(custom) ? custom : Path.Combine(s.DownloadRoot, Key(category));

    /// <summary>"Remember this path for the category" in the download dialog.</summary>
    public static void Remember(AppSettings s, string category, string folder) => s.CategoryFolders[Key(category)] = folder;

    public static string NameFromUrl(Uri url)
    {
        var name = Uri.UnescapeDataString(Path.GetFileName(url.AbsolutePath));
        return string.IsNullOrWhiteSpace(name) ? "download" : name;
    }
}
