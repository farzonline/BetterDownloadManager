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
    public byte[] Body { get; }
    public bool SupportRanges { get; set; } = true;
    public bool IgnoreRange { get; set; }
    public bool RejectHead { get; set; }
    public string? ETag { get; set; }
    public string? ContentDisposition { get; set; }
    public string ContentType { get; set; } = "application/octet-stream";
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
        if (HttpMethods.IsHead(req.Method) && RejectHead) { res.StatusCode = 405; return; }
        res.ContentType = ContentType;
        if (ETag != null) res.Headers.ETag = ETag;
        if (ContentDisposition != null) res.Headers.ContentDisposition = ContentDisposition;
        if (SupportRanges) res.Headers.AcceptRanges = "bytes";

        long start = 0, end = Body.Length - 1;
        var partial = false;
        if (SupportRanges && !IgnoreRange && req.Headers.Range.Count > 0 &&
            TryParseRange(req.Headers.Range[0]!, Body.Length, out start, out end))
        {
            partial = true;
            res.StatusCode = 206;
            res.Headers.ContentRange = $"bytes {start}-{end}/{Body.Length}";
        }
        var len = end - start + 1;
        res.ContentLength = len;
        if (HttpMethods.IsHead(req.Method)) return;
        await res.Body.WriteAsync(Body.AsMemory((int)start, (int)len), ctx.RequestAborted);
        _ = partial;
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
