using BDM.Core.Data;
using BDM.Core.Engine;
using BDM.Core.Streams;

namespace BDM.Core.Tests;

public class DownloadManagerStreamTests : IClassFixture<StreamMedia>
{
    readonly StreamMedia _m;
    static readonly HttpClient Http = new();

    public DownloadManagerStreamTests(StreamMedia m) => _m = m;

    DownloadManager Make()
    {
        var store = new DownloadStore(Path.Combine(Directory.CreateTempSubdirectory().FullName, "bdm.db"));
        return new DownloadManager(store, _ => Http)
        {
            Streams = new StreamDownloader(Http, new FfmpegLocator(_m.Ffmpeg)),
            StreamOptions = new StreamOptions(RetryBaseDelay: TimeSpan.FromMilliseconds(10), LivePollInterval: TimeSpan.FromMilliseconds(100)),
        };
    }

    static async Task<DownloadRecord> WaitFor(DownloadManager m, Guid id, Func<DownloadRecord, bool> cond, int seconds = 30)
    {
        var end = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < end) { var r = m.Get(id); if (r != null && cond(r)) return r; await Task.Delay(25); }
        throw new TimeoutException($"state={m.Get(id)?.State} err={m.Get(id)?.Error}");
    }

    [Fact]
    public async Task AddStream_DownloadsAndCompletes_WithProgress()
    {
        await using var server = new StreamServer(_m.Root);
        var m = Make();
        var src = new StreamSource(server.Url("hls/index.m3u8"), StreamKind.Hls, new Dictionary<string, string>(), null, "Clip", null);
        var choice = (await m.Streams!.ListChoicesAsync(src, default))[0];
        var dir = Directory.CreateTempSubdirectory().FullName;

        var id = m.AddStream(src, choice, dir);

        var done = await WaitFor(m, id, r => r.State == DownloadState.Completed);
        Assert.EndsWith(".mp4", done.FilePath);
        Assert.Equal(6, _m.ProbeFile(done.FilePath!).Duration, 0.6);
        Assert.True(done.Downloaded > 0);
        Assert.NotNull(done.Stream);
    }

    [Fact]
    public async Task AddStream_ProtectedContent_FailsWithProtectedMarker()
    {
        var m = Make();
        var hls = "#EXTM3U\n#EXT-X-TARGETDURATION:4\n#EXT-X-KEY:METHOD=SAMPLE-AES,URI=\"skd://k\"\n#EXTINF:4,\na.ts\n#EXT-X-ENDLIST\n";
        var src = new StreamSource(new Uri("https://x.example/m.m3u8"), StreamKind.Hls, new Dictionary<string, string>(), null, "Locked", hls);
        var choice = (await m.Streams!.ListChoicesAsync(src, default))[0];

        var id = m.AddStream(src, choice, Directory.CreateTempSubdirectory().FullName);

        var failed = await WaitFor(m, id, r => r.State == DownloadState.Failed);
        Assert.Equal(DownloadFailure.Protected, failed.Failure);
        Assert.Contains("DRM", failed.Error);
    }

    [Fact]
    public async Task AddStream_FfmpegMissing_FailsWithActionableMessage()
    {
        await using var server = new StreamServer(_m.Root);
        var m = Make();
        m.Streams = new StreamDownloader(Http, new FfmpegLocator(null, _ => null));
        var good = new StreamDownloader(Http, new FfmpegLocator(_m.Ffmpeg));
        var src = new StreamSource(server.Url("hls/index.m3u8"), StreamKind.Hls, new Dictionary<string, string>(), null, "Clip", null);
        var choice = (await good.ListChoicesAsync(src, default))[0];

        var id = m.AddStream(src, choice, Directory.CreateTempSubdirectory().FullName);

        var failed = await WaitFor(m, id, r => r.State == DownloadState.Failed);
        Assert.Contains("ffmpeg", failed.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Options", failed.Error);
    }

    [Fact]
    public async Task StreamRecord_SurvivesStoreRoundTrip()
    {
        await using var server = new StreamServer(_m.Root);
        var m = Make();
        var src = new StreamSource(server.Url("hls/index.m3u8"), StreamKind.Hls, new Dictionary<string, string> { ["Referer"] = "https://p/" }, "a=b", "Clip", null);
        var choice = (await m.Streams!.ListChoicesAsync(src, default))[0];
        var id = m.AddStream(src, choice, Directory.CreateTempSubdirectory().FullName);
        await WaitFor(m, id, r => r.State == DownloadState.Completed);
        var store = new DownloadStore(Path.Combine(Directory.CreateTempSubdirectory().FullName, "copy.db"));
        store.Import(m.ExportAll());
        var copy = store.Get(id)!;
        Assert.Equal("https://p/", copy.Stream!.Headers["Referer"]);
        Assert.Null(copy.Stream.CookieHeader); // session secrets are dropped once a download is done, and never exported
        Assert.Equal(StreamKind.Hls, copy.Stream.Kind);
        Assert.NotNull(copy.StreamChoice);
    }
}
