using System.Text.RegularExpressions;

namespace BDM.Core.Http;

/// <summary>Identity of a download target with short-lived signature parameters removed (mirrors the extension's dedupe key).</summary>
public static partial class UrlKey
{
    [GeneratedRegex(@"^(x-amz-[\w-]+|token|sig|signature|expires?|exp|e|hdnts|policy|key-pair-id|se|sp|sv|sr|st|oh|oe|bytestart|byteend|range|_nc_\w+)$", RegexOptions.IgnoreCase)]
    private static partial Regex Signature();

    public static string Normalize(Uri url)
    {
        var parts = url.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .Where(kv => !Signature().IsMatch(Uri.UnescapeDataString(kv[0])))
            .Select(kv => kv[0] + "=" + (kv.Length > 1 ? kv[1] : ""))
            .OrderBy(p => p, StringComparer.Ordinal);
        return $"{url.GetLeftPart(UriPartial.Path)}?{string.Join('&', parts)}";
    }
}
