using System.Net;
using System.Net.Http.Headers;
using BDM.Core.Files;

namespace BDM.Core.Http;

public record FileProbe(long? Length, bool AcceptsRanges, string? ETag, string? LastModified, string FileName, string? ContentType);

public static class Prober
{
    public static async Task<FileProbe> ProbeAsync(HttpClient http, DownloadRequest req, CancellationToken ct)
    {
        long? length = null;
        var ranges = false;
        string? etag = null, lastModified = null, contentType = null, disposition = null;

        using (var head = req.CreateMessage(HttpMethod.Head))
        using (var r = await http.SendAsync(head, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            if (r.IsSuccessStatusCode)
            {
                Read(r, ref length, ref etag, ref lastModified, ref contentType, ref disposition);
                ranges = r.Headers.AcceptRanges.Contains("bytes");
            }
        }

        // A server may omit Accept-Ranges (or reject HEAD) yet serve ranges: confirm with a 1-byte GET.
        if (!ranges || length == null)
        {
            using var get = req.CreateMessage(HttpMethod.Get);
            get.Headers.Range = new RangeHeaderValue(0, 0);
            using var r = await http.SendAsync(get, HttpCompletionOption.ResponseHeadersRead, ct);
            r.EnsureSuccessStatusCode();
            if (r.StatusCode == HttpStatusCode.PartialContent)
            {
                ranges = true;
                length = r.Content.Headers.ContentRange?.Length ?? length;
            }
            else
            {
                ranges = false;
                length ??= r.Content.Headers.ContentLength;
            }
            etag ??= r.Headers.ETag?.Tag;
            lastModified ??= r.Content.Headers.LastModified?.ToString("R");
            contentType ??= r.Content.Headers.ContentType?.MediaType;
            disposition ??= r.Content.Headers.ContentDisposition?.ToString();
        }

        var name = req.FileName ?? FileNaming.Resolve(disposition, req.Url, contentType);
        return new FileProbe(length, ranges, etag, lastModified, name, contentType);
    }

    static void Read(HttpResponseMessage r, ref long? length, ref string? etag, ref string? lastModified,
        ref string? contentType, ref string? disposition)
    {
        length = r.Content.Headers.ContentLength;
        etag = r.Headers.ETag?.Tag;
        lastModified = r.Content.Headers.LastModified?.ToString("R");
        contentType = r.Content.Headers.ContentType?.MediaType;
        disposition = r.Content.Headers.ContentDisposition?.ToString();
    }
}
