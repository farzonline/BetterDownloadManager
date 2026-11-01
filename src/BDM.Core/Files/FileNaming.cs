using System.Net.Http.Headers;
using System.Text.RegularExpressions;

namespace BDM.Core.Files;

public static class FileNaming
{
    const int MaxNameLength = 200;
    static readonly Regex Illegal = new(@"[<>:""/\\|?*\x00-\x1f]", RegexOptions.Compiled);
    static readonly Regex Reserved = new(@"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    static readonly Dictionary<string, string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["video/mp4"] = ".mp4", ["video/webm"] = ".webm", ["audio/mpeg"] = ".mp3", ["audio/mp4"] = ".m4a",
        ["application/pdf"] = ".pdf", ["application/zip"] = ".zip", ["image/jpeg"] = ".jpg", ["image/png"] = ".png",
        ["application/vnd.apple.mpegurl"] = ".m3u8", ["application/dash+xml"] = ".mpd",
    };

    public static string Sanitize(string name)
    {
        var s = Illegal.Replace(name, "_").TrimEnd('.', ' ');
        if (s.Length == 0) s = "download";
        var ext = Path.GetExtension(s);
        var stem = Path.GetFileNameWithoutExtension(s);
        if (Reserved.IsMatch(stem)) stem = "_" + stem;
        if (stem.Length + ext.Length > MaxNameLength) stem = stem[..Math.Max(1, MaxNameLength - ext.Length)];
        return stem + ext;
    }

    public static string Unique(string dir, string name)
    {
        if (!File.Exists(Path.Combine(dir, name))) return name;
        var stem = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);
        for (var i = 1; ; i++)
        {
            var candidate = $"{stem} ({i}){ext}";
            if (!File.Exists(Path.Combine(dir, candidate))) return candidate;
        }
    }

    public static string Resolve(string? contentDisposition, Uri url, string? contentType)
    {
        if (contentDisposition != null && ContentDispositionHeaderValue.TryParse(contentDisposition, out var cd))
        {
            var n = cd.FileNameStar ?? cd.FileName?.Trim('"');
            if (!string.IsNullOrWhiteSpace(n)) return Sanitize(n);
        }
        var name = Uri.UnescapeDataString(Path.GetFileName(url.AbsolutePath));
        if (name.Length == 0) name = "download";
        if (Path.GetExtension(name).Length == 0 && contentType != null && Extensions.TryGetValue(contentType, out var ext))
            name += ext;
        return Sanitize(name);
    }
}
