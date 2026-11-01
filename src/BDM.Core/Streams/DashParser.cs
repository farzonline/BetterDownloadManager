using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace BDM.Core.Streams;

/// <summary>Hand-rolled MPD parser (ISO/IEC 23009-1): SegmentTemplate (number/time/timeline), SegmentList, SegmentBase/BaseURL, multi-period.</summary>
public static partial class DashParser
{
    [GeneratedRegex(@"\$(RepresentationID|Number|Bandwidth|Time)(?:%0(\d+)d)?\$|\$\$")]
    private static partial Regex TemplateRegex();

    static XElement? Child(XElement? e, string name) => e?.Elements().FirstOrDefault(c => c.Name.LocalName == name);
    static IEnumerable<XElement> Children(XElement? e, string name) => e?.Elements().Where(c => c.Name.LocalName == name) ?? [];
    static string? Attr(XElement? e, string name) => e?.Attribute(name)?.Value;

    static double? Duration(string? iso)
    {
        if (string.IsNullOrWhiteSpace(iso)) return null;
        try { return XmlConvert.ToTimeSpan(iso).TotalSeconds; } catch (FormatException) { return null; }
    }

    static Uri ResolveBase(Uri parent, XElement el) =>
        Child(el, "BaseURL")?.Value.Trim() is { Length: > 0 } b ? new Uri(parent, b) : parent;

    static string Expand(string template, string repId, int bandwidth, long number, long time) =>
        TemplateRegex().Replace(template, m =>
        {
            if (m.Value == "$$") return "$";
            var width = m.Groups[2].Success ? int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) : 0;
            return m.Groups[1].Value switch
            {
                "RepresentationID" => repId,
                "Bandwidth" => bandwidth.ToString(CultureInfo.InvariantCulture).PadLeft(width, '0'),
                "Number" => number.ToString(CultureInfo.InvariantCulture).PadLeft(width, '0'),
                _ => time.ToString(CultureInfo.InvariantCulture).PadLeft(width, '0'),
            };
        });

    public static DashManifest Parse(string xml, Uri manifestUrl)
    {
        XDocument doc;
        try { doc = XDocument.Parse(xml); }
        catch (XmlException e) { throw new FormatException("Not an MPD document: " + e.Message, e); }
        var mpd = doc.Root;
        if (mpd == null || mpd.Name.LocalName != "MPD") throw new FormatException("Not an MPD document (root element is not MPD).");

        var mpdBase = ResolveBase(manifestUrl, mpd);
        var mpdDuration = Duration(Attr(mpd, "mediaPresentationDuration"));
        var periodElements = Children(mpd, "Period").ToList();
        var periods = new List<DashPeriod>();

        foreach (var period in periodElements)
        {
            var periodBase = ResolveBase(mpdBase, period);
            var periodDuration = Duration(Attr(period, "duration")) ?? (periodElements.Count == 1 ? mpdDuration : null);
            var reps = new List<DashRepresentation>();
            foreach (var set in Children(period, "AdaptationSet"))
            {
                var setBase = ResolveBase(periodBase, set);
                foreach (var rep in Children(set, "Representation"))
                    reps.Add(ParseRepresentation(set, rep, setBase, periodDuration));
            }
            periods.Add(new DashPeriod(reps));
        }
        return new DashManifest(Attr(mpd, "type") == "dynamic", periods);
    }

    static DashRepresentation ParseRepresentation(XElement set, XElement rep, Uri setBase, double? periodDuration)
    {
        var id = Attr(rep, "id") ?? "";
        var bandwidth = int.TryParse(Attr(rep, "bandwidth"), out var bw) ? bw : 0;
        var repBase = ResolveBase(setBase, rep);
        var mime = Attr(rep, "mimeType") ?? Attr(set, "mimeType") ?? (Attr(set, "contentType") is { } ct ? ct + "/mp4" : "");
        var isProtected = Children(rep, "ContentProtection").Any() || Children(set, "ContentProtection").Any();

        var tplSet = Child(set, "SegmentTemplate");
        var tplRep = Child(rep, "SegmentTemplate");
        var list = Child(rep, "SegmentList") ?? Child(set, "SegmentList");
        var urls = new List<Uri>();
        Uri? init = null;
        double duration = periodDuration ?? 0;

        if (tplSet != null || tplRep != null)
        {
            string? A(string n) => Attr(tplRep, n) ?? Attr(tplSet, n);
            var media = A("media") ?? "";
            var timescale = long.Parse(A("timescale") ?? "1", CultureInfo.InvariantCulture);
            var start = long.Parse(A("startNumber") ?? "1", CultureInfo.InvariantCulture);
            if (A("initialization") is { } initT) init = new Uri(repBase, Expand(initT, id, bandwidth, 0, 0));
            var timeline = Child(tplRep, "SegmentTimeline") ?? Child(tplSet, "SegmentTimeline");

            if (timeline != null)
            {
                long t = 0, idx = 0, total = 0;
                foreach (var s in Children(timeline, "S"))
                {
                    if (Attr(s, "t") is { } ts) t = long.Parse(ts, CultureInfo.InvariantCulture);
                    var d = long.Parse(Attr(s, "d")!, CultureInfo.InvariantCulture);
                    var r = long.Parse(Attr(s, "r") ?? "0", CultureInfo.InvariantCulture);
                    var count = r >= 0 ? r + 1 : Math.Max(1, (long)Math.Ceiling(((periodDuration ?? 0) * timescale - t) / d));
                    for (var i = 0; i < count; i++)
                    {
                        urls.Add(new Uri(repBase, Expand(media, id, bandwidth, start + idx++, t)));
                        t += d;
                        total += d;
                    }
                }
                duration = (double)total / timescale;
            }
            else if (A("duration") is { } dur && long.Parse(dur, CultureInfo.InvariantCulture) is var segDur and > 0 && periodDuration is { } pd)
            {
                var count = (long)Math.Ceiling(pd * timescale / segDur);
                for (var i = 0; i < count; i++)
                    urls.Add(new Uri(repBase, Expand(media, id, bandwidth, start + i, i * segDur)));
            }
        }
        else if (list != null)
        {
            if (Attr(Child(list, "Initialization"), "sourceURL") is { } src) init = new Uri(repBase, src);
            urls.AddRange(Children(list, "SegmentURL").Select(s => new Uri(repBase, Attr(s, "media") ?? "")));
            if (periodDuration == null && Attr(list, "duration") is { } ld)
                duration = urls.Count * double.Parse(ld, CultureInfo.InvariantCulture) / double.Parse(Attr(list, "timescale") ?? "1", CultureInfo.InvariantCulture);
        }
        else if (Child(rep, "BaseURL") != null)
        {
            urls.Add(repBase); // one self-contained file (SegmentBase / progressive mp4)
        }

        return new DashRepresentation(id, mime, Attr(rep, "codecs") ?? Attr(set, "codecs"), bandwidth,
            int.TryParse(Attr(rep, "width") ?? Attr(set, "width"), out var w) ? w : null,
            int.TryParse(Attr(rep, "height") ?? Attr(set, "height"), out var h) ? h : null,
            Attr(set, "lang"), init, urls, duration, isProtected);
    }
}
