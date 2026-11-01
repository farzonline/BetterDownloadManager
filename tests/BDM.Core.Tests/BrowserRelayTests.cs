using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BDM.Core.Api;
using BDM.Core.Data;
using BDM.Core.Engine;
using BDM.Core.Http;
using BDM.Core.Net;

namespace BDM.Core.Tests;

/// <summary>
/// Sites behind bot protection only serve a real browser, so BDM's engine can send its requests through the browser:
/// the relay queues them, the extension fetches them in Chrome and streams the bytes back.
/// </summary>
public class BrowserRelayTests
{
    const string ExtensionOrigin = "chrome-extension://" + BrowserRelay.ExtensionId;

    static BrowserRelay Start(TimeSpan? pickup = null)
    {
        var r = new BrowserRelay(RestApi.NewToken(), RestApi.FreePort(), pickup ?? TimeSpan.FromSeconds(10));
        r.Start();
        return r;
    }

    /// <summary>The extension's part, in C#: takes jobs and fetches them "in the browser" (here straight from the test server).</summary>
    sealed class FakeBrowser : IAsyncDisposable
    {
        readonly HttpClient _relay = new();
        readonly HttpClient _web = new();
        readonly CancellationTokenSource _stop = new();
        readonly Task _loop;
        int _jobs, _told;
        public int Jobs => _jobs;
        /// <summary>Times the relay answered 410: BDM no longer wants the rest.</summary>
        public int ToldToStop => _told;
        public string? FailWith { get; set; }

        public FakeBrowser(BrowserRelay relay, int chunk = 16 * 1024)
        {
            _relay.BaseAddress = new Uri($"http://127.0.0.1:{relay.Port}/");
            _relay.DefaultRequestHeaders.Add("X-BDM-Token", relay.Token);
            _relay.DefaultRequestHeaders.Add("Origin", ExtensionOrigin);
            _loop = Task.Run(async () =>
            {
                while (!_stop.IsCancellationRequested)
                {
                    HttpResponseMessage next;
                    try { next = await _relay.GetAsync("relay/next?wait=1", _stop.Token); }
                    catch (OperationCanceledException) { return; }
                    if (next.StatusCode != HttpStatusCode.OK) continue;
                    var job = (await next.Content.ReadFromJsonAsync<JsonElement>())!;
                    Interlocked.Increment(ref _jobs);
                    _ = Task.Run(() => Run(job, chunk));
                }
            });
        }

        async Task Run(JsonElement job, int chunk)
        {
            var id = job.GetProperty("id").GetString();
            if (FailWith != null) { await _relay.PostAsJsonAsync($"relay/{id}/end", new { error = FailWith }); return; }
            using var req = new HttpRequestMessage(new HttpMethod(job.GetProperty("method").GetString()!), job.GetProperty("url").GetString());
            if (job.TryGetProperty("range", out var range) && range.ValueKind == JsonValueKind.String) req.Headers.TryAddWithoutValidation("Range", range.GetString());
            using var res = await _web.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
            var headers = res.Headers.Concat(res.Content.Headers).ToDictionary(h => h.Key, h => string.Join(", ", h.Value));
            await _relay.PostAsJsonAsync($"relay/{id}/head", new { status = (int)res.StatusCode, headers });
            var body = await res.Content.ReadAsStreamAsync();
            var buf = new byte[chunk];
            int n;
            while ((n = await body.ReadAsync(buf)) > 0)
            {
                var r = await _relay.PostAsync($"relay/{id}/data", new ByteArrayContent(buf, 0, n));
                if (r.StatusCode == HttpStatusCode.Gone) { Interlocked.Increment(ref _told); return; }
            }
            await _relay.PostAsJsonAsync($"relay/{id}/end", new { });
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            await _loop;
        }
    }

    [Fact]
    public async Task ARelayedRangeRequest_ReturnsWhatTheBrowserGot()
    {
        await using var server = TestServer.Start(200_000);
        using var relay = Start();
        await using var browser = new FakeBrowser(relay);
        using var http = new HttpClient(relay.Handler);
        using var req = new HttpRequestMessage(HttpMethod.Get, server.FileUrl);
        req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(1000, 99_999);

        using var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);

        Assert.Equal(HttpStatusCode.PartialContent, res.StatusCode);
        Assert.Equal(200_000, res.Content.Headers.ContentRange!.Length);
        Assert.Equal(server.Body[1000..100_000], await res.Content.ReadAsByteArrayAsync()); // arrived in many chunks
    }

    [Fact]
    public async Task AWholeDownload_GoesThroughTheBrowser_WithSeveralConnections()
    {
        await using var server = TestServer.Start(3_000_000);
        using var relay = Start();
        await using var browser = new FakeBrowser(relay, chunk: 256 * 1024);
        var dir = Directory.CreateTempSubdirectory().FullName;
        var store = new DownloadStore(Path.Combine(dir, "bdm.db"));
        store.SaveQueue(new QueueDefinition("main", "Main", 2, 4, null, null, PostAction.None));
        var http = new HttpClient(relay.Handler);
        var m = new DownloadManager(store, id => id == BrowserRelay.ProfileId ? http : throw new InvalidOperationException("must go through the browser"));

        var id = m.Add(new DownloadRequest(server.FileUrl, "clip.bin", null, new Dictionary<string, string>(), null, BrowserRelay.ProfileId, null, dir));
        DownloadRecord? r = null;
        for (var i = 0; i < 600 && r?.State is not (DownloadState.Completed or DownloadState.Failed); i++) { await Task.Delay(50); r = m.Get(id); }

        Assert.True(r!.State == DownloadState.Completed, r.Error);
        Assert.Equal(server.Body, await File.ReadAllBytesAsync(r.FilePath!));
        Assert.True(browser.Jobs >= 3, $"expected the probe plus several parts, got {browser.Jobs} browser fetches");
    }

    [Fact]
    public async Task WithNoBrowserToTakeIt_TheRequestFailsWithAPlainReason()
    {
        using var relay = Start(TimeSpan.FromMilliseconds(300));
        using var http = new HttpClient(relay.Handler);
        var e = await Assert.ThrowsAsync<HttpRequestException>(() => http.GetAsync("https://x.example/a.mp4"));
        Assert.Contains("browser", e.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TheBrowserFailingToFetch_BecomesAnHttpError()
    {
        await using var server = TestServer.Start(1000);
        using var relay = Start();
        await using var browser = new FakeBrowser(relay) { FailWith = "net::ERR_CONNECTION_RESET" };
        using var http = new HttpClient(relay.Handler);
        var e = await Assert.ThrowsAsync<HttpRequestException>(() => http.GetAsync(server.FileUrl));
        Assert.Contains("ERR_CONNECTION_RESET", e.Message);
    }

    [Fact]
    public async Task WhenBdmStopsReading_TheBrowserIsToldToStop()
    {
        await using var server = TestServer.Start(2_000_000);
        using var relay = Start();
        await using var browser = new FakeBrowser(relay, chunk: 8 * 1024);
        using var http = new HttpClient(relay.Handler);
        using (var res = await http.GetAsync(server.FileUrl, HttpCompletionOption.ResponseHeadersRead))
        {
            var s = await res.Content.ReadAsStreamAsync();
            await s.ReadExactlyAsync(new byte[10_000]); // a pause: read a little, then let go
        }
        for (var i = 0; i < 100 && browser.ToldToStop == 0; i++) await Task.Delay(50);
        Assert.Equal(1, browser.ToldToStop);
    }

    [Fact]
    public async Task OnlyTheBdmExtensionWithTheToken_CanTalkToIt()
    {
        using var relay = Start();
        using var c = new HttpClient();
        HttpRequestMessage Req(string? token, string? origin, string host = "127.0.0.1")
        {
            var m = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{relay.Port}/relay/next?wait=0");
            if (token != null) m.Headers.Add("X-BDM-Token", token);
            if (origin != null) m.Headers.Add("Origin", origin);
            m.Headers.Host = $"{host}:{relay.Port}";
            return m;
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.SendAsync(Req(null, ExtensionOrigin))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.SendAsync(Req("wrong", ExtensionOrigin))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.SendAsync(Req(relay.Token, "https://evil.example"))).StatusCode); // a web page
        Assert.Equal(HttpStatusCode.MisdirectedRequest, (await c.SendAsync(Req(relay.Token, ExtensionOrigin, "evil.example"))).StatusCode); // DNS rebinding
        Assert.Equal(HttpStatusCode.NoContent, (await c.SendAsync(Req(relay.Token, ExtensionOrigin))).StatusCode); // nothing queued
    }
}
