using System.Text.RegularExpressions;

namespace BDM.Core.Grabber;

public record GrabOptions(int Depth, string[] IncludeExt, string[] ExcludeExt, bool SameHostOnly);

public static class SiteGrabber
{
    const int MaxPages = 500;
    static readonly Regex Link = new(@"(?:href|src)\s*=\s*[""']([^""']+)[""']", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly HashSet<string> PageExt = new(StringComparer.OrdinalIgnoreCase) { "", "html", "htm", "php", "asp", "aspx", "jsp" };

    /// <summary>Breadth-first crawl; Depth is how many link levels below the start page are followed (0 = start page only).</summary>
    public static async Task<IReadOnlyList<Uri>> CrawlAsync(Uri start, GrabOptions o, HttpClient http, CancellationToken ct)
    {
        var include = new HashSet<string>(o.IncludeExt.Select(e => e.TrimStart('.')), StringComparer.OrdinalIgnoreCase);
        var exclude = new HashSet<string>(o.ExcludeExt.Select(e => e.TrimStart('.')), StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<Uri> { start };
        var found = new HashSet<Uri>();
        var results = new List<Uri>();
        var queue = new Queue<(Uri Url, int Depth)>();
        queue.Enqueue((start, 0));

        while (queue.Count > 0 && visited.Count <= MaxPages)
        {
            ct.ThrowIfCancellationRequested();
            var (page, depth) = queue.Dequeue();
            string html;
            try
            {
                using var resp = await http.GetAsync(page, HttpCompletionOption.ResponseHeadersRead, ct);
                if (!resp.IsSuccessStatusCode || resp.Content.Headers.ContentType?.MediaType?.Contains("html") != true) continue;
                html = await resp.Content.ReadAsStringAsync(ct);
            }
            catch (HttpRequestException) { continue; }

            foreach (Match m in Link.Matches(html))
            {
                if (!Uri.TryCreate(page, m.Groups[1].Value, out var link) || link.Scheme is not ("http" or "https")) continue;
                link = new UriBuilder(link) { Fragment = "" }.Uri;
                if (o.SameHostOnly && !string.Equals(link.Host, start.Host, StringComparison.OrdinalIgnoreCase)) continue;
                var ext = Path.GetExtension(link.AbsolutePath).TrimStart('.');
                if (PageExt.Contains(ext))
                {
                    if (depth < o.Depth && visited.Add(link)) queue.Enqueue((link, depth + 1));
                }
                else if (!exclude.Contains(ext) && (include.Count == 0 || include.Contains(ext)) && found.Add(link))
                    results.Add(link);
            }
        }
        return results;
    }
}
