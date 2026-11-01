using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using BDM.Core.Engine;

namespace BDM.Core.Streams;

/// <summary>Fetches playlists, keys and segments with the browser's headers/cookies, retries, and AES-128 decryption.</summary>
sealed class SegmentFetcher(HttpClient http, StreamSource source, StreamOptions options)
{
    readonly ConcurrentDictionary<Uri, Task<byte[]>> _keys = new();

    HttpRequestMessage Create(Uri url)
    {
        var m = new HttpRequestMessage(HttpMethod.Get, url);
        foreach (var (k, v) in source.Headers) m.Headers.TryAddWithoutValidation(k, v);
        if (source.CookieHeader != null) m.Headers.TryAddWithoutValidation("Cookie", source.CookieHeader);
        return m;
    }

    public async Task<string> GetStringAsync(Uri url, CancellationToken ct) =>
        System.Text.Encoding.UTF8.GetString(await GetBytesAsync(url, null, null, ct));

    public async Task<byte[]> GetBytesAsync(Uri url, long? offset, long? length, CancellationToken ct)
    {
        var baseDelay = options.RetryBaseDelay ?? TimeSpan.FromMilliseconds(500);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var req = Create(url);
                if (offset != null && length != null) req.Headers.Range = new RangeHeaderValue(offset, offset + length - 1);
                using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                var code = (int)resp.StatusCode;
                if (code is 401 or 403) throw new DownloadException(DownloadFailure.AuthExpired, $"The server refused access ({code}); the stream link may have expired.");
                if (code is 404 or 410) throw new DownloadException(DownloadFailure.Network, $"The server answered {code} for {url.AbsolutePath}.");
                if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"Server error {code}", null, resp.StatusCode);
                var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
                // A server that ignored the Range header sent the whole file: cut out the slice.
                if (offset != null && length != null && resp.StatusCode == HttpStatusCode.OK)
                    bytes = bytes.AsSpan((int)offset.Value, (int)Math.Min(length.Value, bytes.Length - offset.Value)).ToArray();
                return bytes;
            }
            catch (Exception e) when ((e is HttpRequestException or IOException) && !ct.IsCancellationRequested)
            {
                if (attempt > options.MaxRetries) throw new DownloadException(DownloadFailure.Network, "Giving up after retries: " + e.Message, e);
                await Task.Delay(TimeSpan.FromMilliseconds(baseDelay.TotalMilliseconds * Math.Pow(2, attempt - 1)), ct);
            }
        }
    }

    /// <summary>One HLS segment, decrypted when it is AES-128 encrypted.</summary>
    public async Task<byte[]> GetSegmentAsync(HlsSegment seg, CancellationToken ct)
    {
        var data = await GetBytesAsync(seg.Url, seg.ByteRangeOffset, seg.ByteRangeLength, ct);
        if (seg.Key is not { Method: "AES-128", Uri: { } keyUri } key) return data;
        var aesKey = await _keys.GetOrAdd(keyUri, u => GetBytesAsync(u, null, null, ct));
        using var aes = Aes.Create();
        aes.Key = aesKey;
        return aes.DecryptCbc(data, key.IvFor(seg.Sequence), PaddingMode.PKCS7);
    }
}
