using System.Diagnostics;
using BDM.Core.Engine;
using BDM.Core.Http;

namespace BDM.Core.Tests;

public class SegmentedDownloaderTests
{
    sealed class SyncProgress(Action<DownloadProgress> on) : IProgress<DownloadProgress>
    {
        public DownloadProgress? Last;
        public void Report(DownloadProgress v) { Last = v; on(v); }
    }

    static readonly TimeSpan FastRetry = TimeSpan.FromMilliseconds(10);

    static async Task<(DownloadRequest Req, FileProbe Probe)> Prep(TestServer s, string dir)
    {
        var req = new DownloadRequest(s.FileUrl, null, null, new Dictionary<string, string>(), null, null, null, dir);
        return (req, await Prober.ProbeAsync(new HttpClient(), req, default));
    }

    static string NewDir() => Directory.CreateTempSubdirectory().FullName;

    [Fact]
    public async Task Downloads_RandomFile_ByteIdentical_With8Connections()
    {
        await using var s = TestServer.Start(1_500_000);
        var dir = NewDir();
        var (req, probe) = await Prep(s, dir);
        var r = await new SegmentedDownloader(new HttpClient()).RunAsync(req, probe, new DownloadOptions(), null, default);
        Assert.Equal(s.Body, await File.ReadAllBytesAsync(r.Path));
        Assert.Equal(1_500_000, r.Bytes);
        Assert.Empty(Directory.GetFiles(dir, "*.bdmpart*"));
    }

    [Fact]
    public async Task Resume_AfterCancel_ContinuesAndIsByteIdentical()
    {
        await using var s = TestServer.Start(1_500_000);
        var dir = NewDir();
        var (req, probe) = await Prep(s, dir);
        var opts = new DownloadOptions(Connections: 4, SpeedLimitBytesPerSec: 500_000);
        using var cts = new CancellationTokenSource();
        var progress = new SyncProgress(p => { if (p.Downloaded > 400_000) cts.Cancel(); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new SegmentedDownloader(new HttpClient()).RunAsync(req, probe, opts, progress, cts.Token));
        Assert.True(File.Exists(Path.Combine(dir, "file.bdmpart")));
        Assert.True(File.Exists(Path.Combine(dir, "file.bdmpart.json")));

        var servedBefore = s.BytesServed;
        var r = await new SegmentedDownloader(new HttpClient()).RunAsync(req, probe, new DownloadOptions(Connections: 4), null, default);
        Assert.Equal(s.Body, await File.ReadAllBytesAsync(r.Path));
        Assert.True(s.BytesServed - servedBefore < 1_500_000 - 300_000, "second run should only fetch the remainder");
    }

    [Fact]
    public async Task Resume_RejectsChangedETag_RestartsFromZero()
    {
        await using var s = TestServer.Start(1_000_000);
        s.ETag = "\"v1\"";
        var dir = NewDir();
        var (req, probe) = await Prep(s, dir);
        using var cts = new CancellationTokenSource();
        var progress = new SyncProgress(p => { if (p.Downloaded > 200_000) cts.Cancel(); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new SegmentedDownloader(new HttpClient()).RunAsync(req, probe, new DownloadOptions(Connections: 4, SpeedLimitBytesPerSec: 400_000), progress, cts.Token));

        var changed = new byte[1_000_000];
        new Random(99).NextBytes(changed);
        s.Body = changed;
        s.ETag = "\"v2\"";
        var probe2 = (await Prep(s, dir)).Probe;
        var r = await new SegmentedDownloader(new HttpClient()).RunAsync(req, probe2, new DownloadOptions(Connections: 4), null, default);
        Assert.Equal(changed, await File.ReadAllBytesAsync(r.Path));
    }

    [Fact]
    public async Task NoRangeSupport_FallsBackToSingleStream()
    {
        await using var s = TestServer.Start(600_000);
        s.SupportRanges = false;
        var (req, probe) = await Prep(s, NewDir());
        var r = await new SegmentedDownloader(new HttpClient()).RunAsync(req, probe, new DownloadOptions(), null, default);
        Assert.Equal(s.Body, await File.ReadAllBytesAsync(r.Path));
    }

    [Fact]
    public async Task ServerIgnoresRange_On200_RestartsCleanlyNoCorruption()
    {
        await using var s = TestServer.Start(600_000);
        var (req, probe) = await Prep(s, NewDir());
        s.IgnoreRange = true; // probe saw "Accept-Ranges: bytes" but the server now answers 200
        var r = await new SegmentedDownloader(new HttpClient()).RunAsync(req, probe, new DownloadOptions(Connections: 4), null, default);
        Assert.Equal(s.Body, await File.ReadAllBytesAsync(r.Path));
        Assert.Equal(600_000, r.Bytes);
    }

    [Fact]
    public async Task ServerIgnoresRange_DuringResume_RestartsCleanlyNoCorruption()
    {
        await using var s = TestServer.Start(1_000_000);
        var dir = NewDir();
        var (req, probe) = await Prep(s, dir);
        using var cts = new CancellationTokenSource();
        var progress = new SyncProgress(p => { if (p.Downloaded > 200_000) cts.Cancel(); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new SegmentedDownloader(new HttpClient()).RunAsync(req, probe, new DownloadOptions(Connections: 4, SpeedLimitBytesPerSec: 400_000), progress, cts.Token));

        s.IgnoreRange = true;
        var r = await new SegmentedDownloader(new HttpClient()).RunAsync(req, probe, new DownloadOptions(Connections: 4), null, default);
        Assert.Equal(s.Body, await File.ReadAllBytesAsync(r.Path));
    }

    [Fact]
    public async Task Expired403_MidDownload_KeepsSidecarAndThrowsAuthExpired()
    {
        await using var s = TestServer.Start(1_000_000);
        s.ExpireAfterBytes = 300_000;
        var dir = NewDir();
        var (req, probe) = await Prep(s, dir);
        var opts = new DownloadOptions(Connections: 4, RetryBaseDelay: FastRetry);
        var ex = await Assert.ThrowsAsync<DownloadException>(() =>
            new SegmentedDownloader(new HttpClient()).RunAsync(req, probe, opts, null, default));
        Assert.Equal(DownloadFailure.AuthExpired, ex.Kind);
        Assert.True(File.Exists(Path.Combine(dir, "file.bdmpart")));
        var side = SegmentSidecar.Load(Path.Combine(dir, "file.bdmpart.json"));
        Assert.NotNull(side);
        Assert.True(side!.Value.Map.Segments.Sum(x => x.Downloaded) > 0);
    }

    [Fact]
    public async Task Retries_TransientFailure_ThenSucceeds()
    {
        await using var s = TestServer.Start(400_000);
        var (req, probe) = await Prep(s, NewDir());
        s.TransientFailures = 2;
        var r = await new SegmentedDownloader(new HttpClient())
            .RunAsync(req, probe, new DownloadOptions(Connections: 4, RetryBaseDelay: FastRetry), null, default);
        Assert.Equal(s.Body, await File.ReadAllBytesAsync(r.Path));
    }

    [Fact]
    public async Task DynamicSplit_FinishedSegmentTakesOverSlowTail()
    {
        await using var s = TestServer.Start(400_000);
        s.SlowFromOffset = 200_000;
        var (req, probe) = await Prep(s, NewDir());
        var progress = new SyncProgress(_ => { });
        var r = await new SegmentedDownloader(new HttpClient())
            .RunAsync(req, probe, new DownloadOptions(Connections: 2, MinSplitBytes: 32 * 1024), progress, default);
        Assert.Equal(s.Body, await File.ReadAllBytesAsync(r.Path));
        Assert.True(progress.Last!.Segments.Count > 2, "slow tail should have been split");
    }

    [Fact]
    public void FromIo_HResult0x70_IsDiskFull()
    {
        var e = DownloadException.FromIo(new IOException("disk full", unchecked((int)0x80070070)));
        Assert.Equal(DownloadFailure.DiskFull, e.Kind);
    }

    [Fact]
    public async Task SpeedLimit_IsHonored()
    {
        await using var s = TestServer.Start(500_000);
        var (req, probe) = await Prep(s, NewDir());
        var sw = Stopwatch.StartNew();
        await new SegmentedDownloader(new HttpClient())
            .RunAsync(req, probe, new DownloadOptions(Connections: 4, SpeedLimitBytesPerSec: 250_000), null, default);
        Assert.True(sw.Elapsed.TotalSeconds >= 1.5, $"took {sw.Elapsed.TotalSeconds:F2}s");
    }

    [Fact]
    public async Task ProgressCallbackThatThrows_DoesNotFailAFinishedDownload()
    {
        await using var s = TestServer.Start(400_000);
        var (req, probe) = await Prep(s, NewDir());
        var r = await new SegmentedDownloader(new HttpClient()).RunAsync(req, probe, new DownloadOptions(Connections: 3, SpeedLimitBytesPerSec: 800_000),
            new SyncProgress(_ => throw new InvalidOperationException("the UI went away")), default);
        Assert.Equal(s.Body, await File.ReadAllBytesAsync(r.Path));
    }
}
