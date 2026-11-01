using System.Text;
using BDM.Core.Data;
using BDM.Core.Engine;
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
}
