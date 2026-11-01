using System.Text;
using BDM.Core.Data;
using BDM.Core.Engine;
using BDM.Core.Ext;
using BDM.Core.Http;
using BDM.Core.Net;
using BDM.Core.Streams;

namespace BDM.Core.Tests;

public class ManagerIntegrationTests
{
    static readonly HttpClient Shared = new();
    static string Tmp() => Directory.CreateTempSubdirectory().FullName;

    static (DownloadManager M, List<string?> ProxyIds, DownloadStore Store) Make(QueueDefinition? queue = null)
    {
        var store = new DownloadStore(Path.Combine(Tmp(), "bdm.db"));
        store.SaveQueue(queue ?? new QueueDefinition("main", "Main", 3, 2, null, null, PostAction.None));
        var ids = new List<string?>();
        var m = new DownloadManager(store, id => { lock (ids) ids.Add(id); return Shared; })
        {
            BaseOptions = new DownloadOptions(RetryBaseDelay: TimeSpan.FromMilliseconds(10)),
        };
        return (m, ids, store);
    }

    static DownloadRequest Req(Uri url, string dir, string? proxy = null, string? category = null) =>
        new(url, null, null, new Dictionary<string, string>(), null, proxy, category, dir);

    static async Task<DownloadRecord> WaitFor(DownloadManager m, Guid id, Func<DownloadRecord, bool> cond, int seconds = 20)
    {
        var end = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < end) { var r = m.Get(id); if (r != null && cond(r)) return r; await Task.Delay(20); }
        throw new TimeoutException($"state={m.Get(id)?.State} err={m.Get(id)?.Error}");
    }

    [Fact]
    public async Task Manager_Ftp_RoutesToFtpDownloader()
    {
        var data = new byte[150_000];
        new Random(8).NextBytes(data);
        using var ftp = new FakeFtp(data);
        var (m, _, _) = Make();
        var id = m.Add(Req(new Uri($"ftp://user:pw@127.0.0.1:{ftp.Port}/data.bin"), Tmp()));
        var done = await WaitFor(m, id, r => r.State is DownloadState.Completed or DownloadState.Failed);
        Assert.True(done.State == DownloadState.Completed, done.Error);
        Assert.Equal(data, await File.ReadAllBytesAsync(done.FilePath!));
        Assert.Equal(data.Length, done.Total);
    }

    [Theory]
    [InlineData("download", "dl")]
    [InlineData("queue", "q")]
    [InlineData("category", "cat")]
    [InlineData("global", "glob")]
    [InlineData("none", null)]
    public async Task Manager_ResolvesProxyProfile_MostSpecificWins(string level, string? expected)
    {
        await using var s = TestServer.Start(20_000);
        var queue = new QueueDefinition("main", "Main", 3, 2, null, null, PostAction.None, level == "queue" ? "q" : null);
        var (m, ids, _) = Make(queue);
        if (level == "category") m.CategoryProxies["Video"] = "cat";
        if (level == "global") m.GlobalProxyId = "glob";
        var id = m.Add(Req(s.FileUrl, Tmp(), proxy: level == "download" ? "dl" : null, category: "Video"));
        await WaitFor(m, id, r => r.State == DownloadState.Completed);
        lock (ids) Assert.Equal(expected, ids.First());
    }

    [Fact]
    public void Manager_StreamDownloaderFor_UsesTheRoutedClient()
    {
        var (m, ids, _) = Make();
        var built = new List<HttpClient>();
        m.StreamsFor = c => { built.Add(c); return new StreamDownloader(c, new FfmpegLocator(null, _ => null)); };
        m.GlobalProxyId = "glob";
        Assert.NotNull(m.StreamDownloaderFor(m.ProxyIdFor("Video", "main")));
        Assert.Single(built);
        lock (ids) Assert.Equal("glob", ids.Last());
        m.StreamsFor = null;
        m.Streams = new StreamDownloader(Shared, new FfmpegLocator(null, _ => null));
        Assert.Same(m.Streams, m.StreamDownloaderFor(null));
    }

    [Fact]
    public async Task Manager_SendsBasicAuthFromSiteLogin_AndFailsWithoutOne()
    {
        await using var s = TestServer.Start(30_000);
        s.RequiredAuthorization = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("bob:pw"));

        var (without, _, _) = Make();
        var bad = without.Add(Req(s.FileUrl, Tmp()));
        var failed = await WaitFor(without, bad, r => r.State == DownloadState.Failed);
        Assert.Equal(DownloadFailure.AuthExpired, failed.Failure);

        var (with, _, _) = Make();
        var vault = new CredentialVault(Path.Combine(Tmp(), "v.json"));
        with.Logins = new SiteLogins(Path.Combine(Tmp(), "logins.json"), vault);
        with.Logins.Set(s.BaseUri.Host, "bob", "pw");
        var ok = with.Add(Req(s.FileUrl, Tmp()));
        var done = await WaitFor(with, ok, r => r.State is DownloadState.Completed or DownloadState.Failed);
        Assert.True(done.State == DownloadState.Completed, done.Error);
        lock (s.Authorizations) Assert.Contains(s.RequiredAuthorization, s.Authorizations);
    }

    [Fact]
    public async Task Manager_OnCompleted_IsCalledWithTheFinishedRecord()
    {
        await using var s = TestServer.Start(20_000);
        var (m, _, _) = Make();
        var tcs = new TaskCompletionSource<DownloadRecord>();
        m.OnCompleted = rec => { tcs.TrySetResult(rec); return Task.CompletedTask; };
        var id = m.Add(Req(s.FileUrl, Tmp()));
        var rec = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(id, rec.Id);
        Assert.Equal(DownloadState.Completed, rec.State);
        Assert.True(File.Exists(rec.FilePath));
    }

    [Fact]
    public async Task Manager_OnCompleted_ThrowingHookDoesNotBreakTheDownload()
    {
        await using var s = TestServer.Start(20_000);
        var (m, _, _) = Make();
        m.OnCompleted = _ => throw new InvalidOperationException("scanner exploded");
        var id = m.Add(Req(s.FileUrl, Tmp()));
        var done = await WaitFor(m, id, r => r.State == DownloadState.Completed);
        Assert.Null(done.Error);
    }

    [Fact]
    public async Task Manager_GlobalLimiter_SharesOneBudgetAcrossDownloads()
    {
        await using var s = TestServer.Start(300_000);
        var (m, _, _) = Make();
        m.GlobalLimiter = new SpeedLimiter(400_000); // 600 KB in total at 400 KB/s is about 1.5 s
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var a = m.Add(Req(s.FileUrl, Tmp()));
        var b = m.Add(Req(s.FileUrl, Tmp()));
        await WaitFor(m, a, r => r.State == DownloadState.Completed);
        await WaitFor(m, b, r => r.State == DownloadState.Completed);
        Assert.True(sw.Elapsed.TotalSeconds >= 1.2, $"finished in {sw.Elapsed.TotalSeconds:F2}s: the two downloads did not share the limit");

        m.GlobalLimiter.SetLimit(0); // unlimited again, live
        var sw2 = System.Diagnostics.Stopwatch.StartNew();
        var c = m.Add(Req(s.FileUrl, Tmp()));
        await WaitFor(m, c, r => r.State == DownloadState.Completed);
        Assert.True(sw2.Elapsed.TotalSeconds < 1.0, $"unlimited download took {sw2.Elapsed.TotalSeconds:F2}s");
    }

    [Fact]
    public void Manager_ReloadFromStore_PicksUpImportedDownloads_AsPaused_AndIsIdempotent()
    {
        var (m, _, store) = Make();
        var rec = new DownloadRecord(Guid.NewGuid(), Req(new Uri("https://x/a.zip"), "."), DownloadState.Running, 5, 10, null, "main", DateTime.UtcNow);
        store.Upsert(rec); // what a settings import does
        var seen = new List<Guid>();
        m.Changed += r => seen.Add(r.Id);

        Assert.Equal(1, m.ReloadFromStore());

        Assert.Equal(DownloadState.Paused, m.Get(rec.Id)!.State); // nothing is running yet, so it cannot be "Running"
        Assert.Contains(rec.Id, seen);
        Assert.Equal(0, m.ReloadFromStore());
    }

    sealed class FakeYtDlp(Func<IReadOnlyList<string>, (int Exit, string Err)> behave) : IYtDlpProcess
    {
        public List<IReadOnlyList<string>> Calls { get; } = new();
        public Task<(int Exit, string StdOut, string StdErr)> RunAsync(IReadOnlyList<string> args, Action<string>? onLine, CancellationToken ct)
        {
            Calls.Add(args);
            var (exit, err) = behave(args);
            if (exit == 0)
            {
                var dir = Path.GetDirectoryName(args[args.ToList().IndexOf("-o") + 1])!;
                var file = Path.Combine(dir, "Clip.mp4");
                File.WriteAllBytes(file, new byte[4096]);
                onLine?.Invoke("BDMPROGRESS 2048 4096 NA");
                onLine?.Invoke("BDMFILE " + file);
            }
            return Task.FromResult((exit, "", err));
        }
    }

    [Fact]
    public async Task Manager_YtDlpJob_CompletesAndRoutesThroughTheProxy()
    {
        var (m, _, _) = Make();
        var fake = new FakeYtDlp(_ => (0, ""));
        m.YtDlp = new YtDlpRunner(() => "yt-dlp.exe", _ => fake);
        m.GlobalProxyId = "p1";
        m.ProxyUrlFor = id => id == "p1" ? "socks5://u:pw@h:1080" : null;
        var choice = new BDM.Core.Streams.StreamChoice("720p", new BDM.Core.Streams.StreamTrack("bv*[height<=720]+ba/b[height<=720]", new Uri("https://v.example/watch?v=1")), null, []);

        var id = m.AddYtDlp(new Uri("https://v.example/watch?v=1"), choice, Tmp(), new Dictionary<string, string> { ["Referer"] = "https://v.example/" }, "a=b");

        var done = await WaitFor(m, id, r => r.State is DownloadState.Completed or DownloadState.Failed);
        Assert.True(done.State == DownloadState.Completed, done.Error);
        Assert.EndsWith("Clip.mp4", done.FilePath);
        Assert.Equal(4096, done.Total);
        var args = fake.Calls.Single();
        Assert.Equal("bv*[height<=720]+ba/b[height<=720]", args[args.ToList().IndexOf("-f") + 1]);
        Assert.Equal("socks5://u:pw@h:1080", args[args.ToList().IndexOf("--proxy") + 1]);
        Assert.Contains("Cookie: a=b", args);
        Assert.Contains("Referer: https://v.example/", args);
    }

    [Fact]
    public async Task Manager_YtDlpJob_Failure_ShowsYtDlpsOwnMessage()
    {
        var (m, _, _) = Make();
        m.YtDlp = new YtDlpRunner(() => "yt-dlp.exe", _ => new FakeYtDlp(_ => (1, "ERROR: This video is private")));
        var choice = new BDM.Core.Streams.StreamChoice("Best", new BDM.Core.Streams.StreamTrack("b", new Uri("https://v.example/w")), null, []);
        var id = m.AddYtDlp(new Uri("https://v.example/w"), choice, Tmp(), null, null);
        var failed = await WaitFor(m, id, r => r.State == DownloadState.Failed);
        Assert.Contains("private", failed.Error);
    }

    [Fact]
    public async Task Manager_YtDlpJob_NotConfigured_FailsWithAnActionableMessage()
    {
        var (m, _, _) = Make();
        var choice = new BDM.Core.Streams.StreamChoice("Best", new BDM.Core.Streams.StreamTrack("b", new Uri("https://v.example/w")), null, []);
        var id = m.AddYtDlp(new Uri("https://v.example/w"), choice, Tmp(), null, null);
        var failed = await WaitFor(m, id, r => r.State == DownloadState.Failed);
        Assert.Contains("yt-dlp", failed.Error);
        Assert.Contains("Options", failed.Error);
    }
}
