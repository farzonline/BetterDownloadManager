using System.Security.Cryptography;
using BDM.Core.Data;
using BDM.Core.Engine;
using BDM.Core.Http;

namespace BDM.Core.Tests;

public class DownloadManagerTests
{
    static readonly HttpClient Shared = new();

    static (DownloadManager M, DownloadStore S, string Dir) Make(int maxConcurrent = 3, int connections = 2)
    {
        var store = new DownloadStore(Path.Combine(Directory.CreateTempSubdirectory().FullName, "bdm.db"));
        store.SaveQueue(new QueueDefinition("main", "Main", maxConcurrent, connections, null, null, PostAction.None));
        var m = new DownloadManager(store, _ => Shared) { BaseOptions = new DownloadOptions(RetryBaseDelay: TimeSpan.FromMilliseconds(10)) };
        return (m, store, Directory.CreateTempSubdirectory().FullName);
    }

    static DownloadRequest Req(TestServer s, string dir) =>
        new(s.FileUrl, null, null, new Dictionary<string, string>(), null, null, null, dir);

    static async Task<DownloadRecord> WaitFor(DownloadManager m, Guid id, Func<DownloadRecord, bool> cond, int seconds = 20)
    {
        var end = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < end)
        {
            var r = m.Get(id);
            if (r != null && cond(r)) return r;
            await Task.Delay(20);
        }
        throw new TimeoutException($"condition not met; state={m.Get(id)?.State} err={m.Get(id)?.Error}");
    }

    [Fact]
    public async Task Manager_RespectsQueueMaxConcurrent()
    {
        await using var s = TestServer.Start(120_000);
        s.SlowFromOffset = 0;
        var (m, _, dir) = Make(maxConcurrent: 2, connections: 1);
        var states = new Dictionary<Guid, DownloadState>();
        var maxRunning = 0;
        m.Changed += r => { lock (states) { states[r.Id] = r.State; maxRunning = Math.Max(maxRunning, states.Values.Count(v => v == DownloadState.Running)); } };
        var ids = Enumerable.Range(0, 3).Select(i => m.Add(Req(s, Path.Combine(dir, "d" + i)))).ToList();
        foreach (var id in ids) await WaitFor(m, id, r => r.State == DownloadState.Completed);
        Assert.Equal(2, maxRunning);
    }

    [Fact]
    public async Task Manager_PauseThenResume_Completes()
    {
        await using var s = TestServer.Start(600_000);
        s.SlowFromOffset = 0; s.SlowDelayMs = 60;
        var (m, _, dir) = Make(connections: 2);
        var id = m.Add(Req(s, dir));
        await WaitFor(m, id, r => r.State == DownloadState.Running && r.Downloaded > 0);
        m.Pause(id);
        var paused = await WaitFor(m, id, r => r.State == DownloadState.Paused);
        Assert.True(paused.Downloaded < 600_000);
        m.Resume(id);
        var done = await WaitFor(m, id, r => r.State == DownloadState.Completed);
        Assert.Equal(s.Body, await File.ReadAllBytesAsync(done.FilePath!));
    }

    [Fact]
    public async Task Manager_DuplicateUrl_FlagsDuplicate()
    {
        await using var s = TestServer.Start(10_000);
        var (m, _, dir) = Make();
        var a = m.Add(Req(s, Path.Combine(dir, "a")));
        var b = m.Add(Req(s, Path.Combine(dir, "b")));
        Assert.False(m.Get(a)!.Duplicate);
        Assert.True(m.Get(b)!.Duplicate);
    }

    [Fact]
    public async Task Manager_Completion_VerifiesChecksumWhenProvided()
    {
        await using var s = TestServer.Start(50_000);
        var (m, _, dir) = Make();
        var good = Convert.ToHexString(SHA256.HashData(s.Body)).ToLowerInvariant();
        var ok = m.Add(Req(s, Path.Combine(dir, "ok")), expectedSha256: good);
        var bad = m.Add(Req(s, Path.Combine(dir, "bad")), expectedSha256: new string('0', 64));
        await WaitFor(m, ok, r => r.State == DownloadState.Completed);
        var failed = await WaitFor(m, bad, r => r.State == DownloadState.Failed);
        Assert.Contains("checksum", failed.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Manager_AuthExpired_SetsFailedWithResumableMarker()
    {
        await using var s = TestServer.Start(600_000);
        s.ExpireAfterBytes = 100_000;
        var (m, _, dir) = Make(connections: 2);
        var id = m.Add(Req(s, dir));
        var r = await WaitFor(m, id, x => x.State == DownloadState.Failed);
        Assert.Equal(DownloadFailure.AuthExpired, r.Failure);
        Assert.True(File.Exists(Path.Combine(dir, "file.bdmpart")), "progress must be kept");
    }

    [Fact]
    public async Task Manager_SegmentsFor_ReportsLiveSegmentsWhileRunning_AndNothingAfter()
    {
        await using var s = TestServer.Start(600_000);
        s.SlowFromOffset = 0; s.SlowDelayMs = 40;
        var (m, _, dir) = Make(connections: 3);
        var id = m.Add(Req(s, dir));
        await WaitFor(m, id, r => r.State == DownloadState.Running && r.Downloaded > 0);
        var live = m.SegmentsFor(id);
        Assert.NotNull(live);
        Assert.True(live!.Count >= 3);
        Assert.Equal(600_000, live.Max(x => x.End) + 1);
        await WaitFor(m, id, r => r.State == DownloadState.Completed);
        Assert.Null(m.SegmentsFor(id));
        Assert.Null(m.SegmentsFor(Guid.NewGuid()));
    }
}
