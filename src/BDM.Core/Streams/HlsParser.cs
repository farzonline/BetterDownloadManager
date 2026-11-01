using System.Globalization;
using System.Text.RegularExpressions;

namespace BDM.Core.Streams;

/// <summary>Hand-rolled RFC 8216 parser for master and media playlists.</summary>
public static partial class HlsParser
{
    [GeneratedRegex("""([A-Z0-9-]+)=("([^"]*)"|[^,]*)""")]
    private static partial Regex AttrRegex();

    static Dictionary<string, string> Attrs(string list) =>
        AttrRegex().Matches(list).ToDictionary(m => m.Groups[1].Value, m => m.Groups[3].Success ? m.Groups[3].Value : m.Groups[2].Value);

    static string? Get(Dictionary<string, string> a, string key) => a.GetValueOrDefault(key);

    static (long Length, long? Offset) ParseRange(string s)
    {
        var parts = s.Split('@');
        return (long.Parse(parts[0], CultureInfo.InvariantCulture), parts.Length > 1 ? long.Parse(parts[1], CultureInfo.InvariantCulture) : null);
    }

    public static HlsPlaylist Parse(string text, Uri baseUri)
    {
        var lines = text.TrimStart('﻿').Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        if (lines.Count == 0 || lines[0] != "#EXTM3U") throw new FormatException("Not an HLS playlist (missing #EXTM3U).");

        var variants = new List<HlsVariant>();
        var media = new List<HlsMedia>();
        var segments = new List<HlsSegment>();
        HlsMap? map = null;
        HlsKey? key = null;
        var ended = false;
        double target = 0, pendingDuration = -1;
        long sequence = 0;
        (long Length, long? Offset)? pendingRange = null;
        Dictionary<string, string>? pendingStreamInf = null;
        var rangeEnd = new Dictionary<Uri, long>();

        Uri Resolve(string s) => new(baseUri, s);

        foreach (var line in lines.Skip(1))
        {
            if (line.StartsWith('#'))
            {
                var colon = line.IndexOf(':');
                var tag = colon < 0 ? line : line[..colon];
                var value = colon < 0 ? "" : line[(colon + 1)..];
                switch (tag)
                {
                    case "#EXT-X-TARGETDURATION": target = double.Parse(value, CultureInfo.InvariantCulture); break;
                    case "#EXT-X-MEDIA-SEQUENCE": sequence = long.Parse(value, CultureInfo.InvariantCulture); break;
                    case "#EXT-X-ENDLIST": ended = true; break;
                    case "#EXTINF":
                        pendingDuration = double.Parse(value.Split(',')[0], CultureInfo.InvariantCulture);
                        break;
                    case "#EXT-X-BYTERANGE": pendingRange = ParseRange(value); break;
                    case "#EXT-X-STREAM-INF": pendingStreamInf = Attrs(value); break;
                    case "#EXT-X-MEDIA":
                    {
                        var a = Attrs(value);
                        media.Add(new HlsMedia(Get(a, "TYPE") ?? "", Get(a, "GROUP-ID") ?? "",
                            Get(a, "URI") is { } u ? Resolve(u) : null, Get(a, "LANGUAGE"), Get(a, "NAME")));
                        break;
                    }
                    case "#EXT-X-KEY":
                    {
                        var a = Attrs(value);
                        var method = Get(a, "METHOD") ?? "NONE";
                        if (method == "NONE") { key = null; break; }
                        byte[]? iv = Get(a, "IV") is { } hex ? Convert.FromHexString(hex.Replace("0x", "").Replace("0X", "").PadLeft(32, '0')) : null;
                        key = new HlsKey(method, Get(a, "URI") is { } ku ? Resolve(ku) : null, iv);
                        break;
                    }
                    case "#EXT-X-MAP":
                    {
                        var a = Attrs(value);
                        if (map == null && Get(a, "URI") is { } mu)
                        {
                            var r = Get(a, "BYTERANGE") is { } br ? ParseRange(br) : ((long, long?)?)null;
                            map = new HlsMap(Resolve(mu), r?.Item2 ?? (r != null ? 0 : null), r?.Item1);
                        }
                        break;
                    }
                }
                continue;
            }

            // A URI line belongs to the preceding STREAM-INF or EXTINF.
            if (pendingStreamInf != null)
            {
                var a = pendingStreamInf;
                var bw = Get(a, "BANDWIDTH") ?? Get(a, "AVERAGE-BANDWIDTH");
                variants.Add(new HlsVariant(Resolve(line), bw != null ? int.Parse(bw, CultureInfo.InvariantCulture) : 0,
                    Get(a, "RESOLUTION"), Get(a, "CODECS"), Get(a, "AUDIO"), Get(a, "SUBTITLES")));
                pendingStreamInf = null;
            }
            else if (pendingDuration >= 0)
            {
                var url = Resolve(line);
                long? offset = null, length = null;
                if (pendingRange is { } pr)
                {
                    length = pr.Length;
                    offset = pr.Offset ?? rangeEnd.GetValueOrDefault(url);
                    rangeEnd[url] = offset.Value + length.Value;
                }
                segments.Add(new HlsSegment(url, pendingDuration, sequence++, offset, length, key));
                pendingDuration = -1;
                pendingRange = null;
            }
        }

        return new HlsPlaylist(variants.Count > 0, variants, media, segments, map, ended, target);
    }
}
