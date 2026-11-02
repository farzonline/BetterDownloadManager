using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BDM.Core.Tests;

/// <summary>In-process Kestrel server serving one configurable file at /file.</summary>
public sealed class TestServer : IAsyncDisposable
{
    readonly WebApplication _app;
    long _bytesServed;
    int _transientFailures;
    int _openBodies;
    volatile bool _expired;

    public byte[] Body { get; set; }
    public bool SupportRanges { get; set; } = true;
    /// <summary>Advertise ranges but answer every request with 200 and the whole body.</summary>
    public bool IgnoreRange { get; set; }
    public bool RejectHead { get; set; }
    /// <summary>GETs answer 204 No Content (what Chrome hands back for a request it turned into its own download).</summary>
    public bool NoContentForGets { get; set; }
    public string? ETag { get; set; }
    /// <summary>Like free file hosts (Hotlink): a GET while this many are already sending gets 503.</summary>
    public int MaxConnections { get; set; } = int.MaxValue;
    public int Refused;
    /// <summary>Most GETs that were sending at the same moment.</summary>
    public int PeakConnections;
    public string? ContentDisposition { get; set; }
    public string ContentType { get; set; } = "application/octet-stream";
    /// <summary>First N GET requests answer 500.</summary>
    public int TransientFailures { get => _transientFailures; set => _transientFailures = value; }
    /// <summary>Once this many body bytes were served, abort the connection and answer 403 from then on.</summary>
    public long ExpireAfterBytes { get; set; } = -1;
    /// <summary>Ranges starting at or beyond this offset are served slowly.</summary>
    public long SlowFromOffset { get; set; } = long.MaxValue;
    public int SlowDelayMs { get; set; } = 15;
    /// <summary>When set, requests without exactly this Authorization header get 401.</summary>
    public string? RequiredAuthorization { get; set; }
    public List<string?> Authorizations { get; } = new();
    public long BytesServed => Interlocked.Read(ref _bytesServed);
    /// <summary>Makes the signed link valid again (the owner opened the page and got a fresh one).</summary>
    public void ResetExpiry() { ExpireAfterBytes = -1; _expired = false; }
    public Uri BaseUri { get; private set; } = null!;
    public Uri FileUrl => new(BaseUri, "/file");

    TestServer(byte[] body)
    {
        Body = body;
        var b = WebApplication.CreateBuilder();
        b.Logging.ClearProviders();
        b.WebHost.UseUrls("http://127.0.0.1:0");
        _app = b.Build();
        _app.MapFallback(Handle);
    }

    public static TestServer Start(int length, int seed = 1)
    {
        var body = new byte[length];
        new Random(seed).NextBytes(body);
        var s = new TestServer(body);
        s._app.StartAsync().GetAwaiter().GetResult();
        s.BaseUri = new Uri(s._app.Urls.First());
        return s;
    }

    async Task Handle(HttpContext ctx)
    {
        var req = ctx.Request;
        var res = ctx.Response;
        var isHead = HttpMethods.IsHead(req.Method);
        var auth = req.Headers.Authorization.Count > 0 ? req.Headers.Authorization[0] : null;
        lock (Authorizations) Authorizations.Add(auth);
        if (RequiredAuthorization != null && auth != RequiredAuthorization) { res.StatusCode = 401; return; }
        if (isHead && RejectHead) { res.StatusCode = 405; return; }
        if (!isHead && NoContentForGets) { res.StatusCode = 204; return; }
        if (!isHead)
        {
            if (_expired) { res.StatusCode = 403; return; }
            if (Interlocked.Decrement(ref _transientFailures) >= 0) { res.StatusCode = 500; return; }
        }
        var body = Body;
        res.ContentType = ContentType;
        if (ETag != null) res.Headers.ETag = ETag;
        if (ContentDisposition != null) res.Headers.ContentDisposition = ContentDisposition;
        if (SupportRanges) res.Headers.AcceptRanges = "bytes";

        long start = 0, end = body.Length - 1;
        if (SupportRanges && !IgnoreRange && req.Headers.Range.Count > 0 &&
            TryParseRange(req.Headers.Range[0]!, body.Length, out start, out end))
        {
            res.StatusCode = 206;
            res.Headers.ContentRange = $"bytes {start}-{end}/{body.Length}";
        }
        var len = end - start + 1;
        res.ContentLength = len;
        if (isHead) return;

        var open = Interlocked.Increment(ref _openBodies);
        int peak;
        while (open > (peak = PeakConnections) && Interlocked.CompareExchange(ref PeakConnections, open, peak) != peak) { }
        if (open > MaxConnections)
        {
            Interlocked.Decrement(ref _openBodies);
            Interlocked.Increment(ref Refused);
            res.Headers.Remove("Content-Range");
            res.ContentLength = 0;
            res.StatusCode = 503;
            return;
        }
        try
        {
            const int chunk = 8192;
            for (long pos = start; pos <= end; pos += chunk)
            {
                if (ExpireAfterBytes >= 0 && BytesServed >= ExpireAfterBytes) { _expired = true; ctx.Abort(); return; }
                var n = (int)Math.Min(chunk, end - pos + 1);
                await res.Body.WriteAsync(body.AsMemory((int)pos, n), ctx.RequestAborted);
                Interlocked.Add(ref _bytesServed, n);
                if (start >= SlowFromOffset) await Task.Delay(SlowDelayMs, ctx.RequestAborted);
            }
        }
        finally { Interlocked.Decrement(ref _openBodies); }
    }

    static bool TryParseRange(string header, long total, out long start, out long end)
    {
        start = 0; end = total - 1;
        if (!header.StartsWith("bytes=")) return false;
        var parts = header[6..].Split('-');
        if (!long.TryParse(parts[0], out start)) return false;
        end = parts.Length > 1 && long.TryParse(parts[1], out var e) ? Math.Min(e, total - 1) : total - 1;
        return start <= end;
    }

    public async ValueTask DisposeAsync() { await _app.StopAsync(); await _app.DisposeAsync(); }
}
