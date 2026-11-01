using BDM.App.Settings;

namespace BDM.App.Services;

/// <summary>Decides whether clipboard text is a link worth offering to download (once per distinct text).</summary>
public sealed class ClipboardUrlFilter(Func<AppSettings> settings)
{
    string? _last;

    public bool TryGet(string? text, out Uri? url)
    {
        url = null;
        if (text == null) return false;
        var trimmed = text.Trim();
        if (trimmed == _last) return false;
        _last = trimmed;
        if (trimmed.Length == 0 || trimmed.Any(char.IsWhiteSpace)) return false;
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https" or "ftp" or "ftps")) return false;
        var ext = Path.GetExtension(u.AbsolutePath).TrimStart('.');
        if (!settings().ClipboardTypes.Contains(ext, StringComparer.OrdinalIgnoreCase)) return false;
        url = u;
        return true;
    }
}
