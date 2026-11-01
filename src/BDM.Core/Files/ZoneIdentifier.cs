namespace BDM.Core.Files;

/// <summary>
/// Mark-of-the-Web: tags a finished download as coming from the Internet (NTFS stream "Zone.Identifier", zone 3) so Windows
/// SmartScreen and Office Protected View treat it like a browser download.
/// </summary>
public static class ZoneIdentifier
{
    /// <returns>false when the file is missing or the volume cannot hold alternate streams (e.g. FAT32); never throws.</returns>
    public static bool Mark(string path, Uri? hostUrl, string? referrer)
    {
        try
        {
            if (!File.Exists(path)) return false;
            var text = "[ZoneTransfer]\r\nZoneId=3\r\n"
                + (string.IsNullOrWhiteSpace(referrer) ? "" : $"ReferrerUrl={StripQuery(referrer)}\r\n")
                + (hostUrl == null ? "" : $"HostUrl={hostUrl.GetLeftPart(UriPartial.Path)}\r\n"); // signed query strings stay out of the file
            File.WriteAllText(path + ":Zone.Identifier", text);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException) { return false; }
    }

    static string StripQuery(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.GetLeftPart(UriPartial.Path) : url;
}
