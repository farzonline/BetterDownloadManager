using BDM.Core.Data;
using BDM.Core.Engine;
using BDM.Core.Http;

namespace BDM.Core.Tests;

public class SchedulerTests
{
    sealed class FakePower : IPowerApi
    {
        public List<string> Calls { get; } = new();
        public void Sleep() => Calls.Add("sleep");
        public void Hibernate() => Calls.Add("hibernate");
        public void Shutdown() => Calls.Add("shutdown");
        public void Open(string path) => Calls.Add("open:" + path);
        public void Start(string program, string args) => Calls.Add($"start:{program} {args}");
    }

    static QueueDefinition Q(TimeSpan? start, TimeSpan? stop, PostAction a = PostAction.None) =>
        new("main", "Main", 3, 2, start, stop, a);

    static DateTime At(int h, int m = 0) => new(2026, 11, 1, h, m, 0);

    [Fact]
    public void Window_Overnight_23_to_06_OpenAt0030_ClosedAt1200()
    {
        var q = Q(TimeSpan.FromHours(23), TimeSpan.FromHours(6));
        Assert.True(Scheduler.IsWindowOpen(q, At(0, 30)));
        Assert.True(Scheduler.IsWindowOpen(q, At(23)));
        Assert.False(Scheduler.IsWindowOpen(q, At(12)));
        Assert.False(Scheduler.IsWindowOpen(q, At(6)));
    }

    [Fact]
    public void Window_SameDay_9_to_17()
    {
        var q = Q(TimeSpan.FromHours(9), TimeSpan.FromHours(17));
        Assert.True(Scheduler.IsWindowOpen(q, At(9)));
        Assert.False(Scheduler.IsWindowOpen(q, At(8, 59)));
        Assert.False(Scheduler.IsWindowOpen(q, At(17)));
    }

    [Fact]
    public void Window_Null_AlwaysOpen() => Assert.True(Scheduler.IsWindowOpen(Q(null, null), At(3)));

    [Fact]
    public void PostAction_Shutdown_CallsPowerApiOnce()
    {
        var p = new FakePower();
        PostActions.Run(PostAction.Shutdown, null, null, p);
        Assert.Equal(new[] { "shutdown" }, p.Calls);
    }

    [Fact]
    public void PostAction_OpenFileAndRunProgram_PassThePath()
    {
        var p = new FakePower();
        PostActions.Run(PostAction.OpenFile, @"C:\a.zip", null, p);
        PostActions.Run(PostAction.RunProgram, @"C:\a.zip", @"C:\tool.exe", p);
        PostActions.Run(PostAction.None, @"C:\a.zip", null, p);
        Assert.Equal(new[] { @"open:C:\a.zip", "start:C:\\tool.exe \"C:\\a.zip\"" }, p.Calls);
    }

    // ---- manager integration ----
    static readonly HttpClient Shared = new();

    static (DownloadManager M, string Dir) Make(QueueDefinition q)
    {
        var store = new DownloadStore(Path.Combine(Directory.CreateTempSubdirectory().FullName, "bdm.db"));
        store.SaveQueue(q);
        var m = new DownloadManager(store, _ => Shared) { BaseOptions = new DownloadOptions(MaxRetries: 1, RetryBaseDelay: TimeSpan.FromMilliseconds(10)) };
        return (m, Directory.CreateTempSubdirectory().FullName);
    }

    static DownloadRequest Req(TestServer s, string dir) =>
        new(s.FileUrl, null, null, new Dictionary<string, string>(), null, null, null, dir);

    static async Task<DownloadRecord> WaitFor(DownloadManager m, Guid id, Func<DownloadRecord, bool> cond, int seconds = 20)
    {
        var end = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < end) { var r = m.Get(id); if (r != null && cond(r)) return r; await Task.Delay(20); }
        throw new TimeoutException($"state={m.Get(id)?.State} err={m.Get(id)?.Error}");
    }

    [Fact]
    public async Task Manager_NetworkRestored_ResumesNetworkFailedDownloads_NotAuthExpired()
    {
        await using var flaky = TestServer.Start(100_000);
        await using var expiring = TestServer.Start(600_000);
        flaky.TransientFailures = 1000;
        expiring.ExpireAfterBytes = 50_000;
        var (m, dir) = Make(Q(null, null));
        var watcher = new ConnectivityWatcher(listen: false);
        m.Watch(watcher);
        var net = m.Add(Req(flaky, Path.Combine(dir, "n")));
        var auth = m.Add(Req(expiring, Path.Combine(dir, "a")));
        await WaitFor(m, net, r => r.State == DownloadState.Failed && r.Failure == DownloadFailure.Network);
        await WaitFor(m, auth, r => r.State == DownloadState.Failed && r.Failure == DownloadFailure.AuthExpired);

        flaky.TransientFailures = 0;
        watcher.NotifyRestored();
        await WaitFor(m, net, r => r.State == DownloadState.Completed);
        await Task.Delay(300);
        Assert.Equal(DownloadState.Failed, m.Get(auth)!.State);
    }

    [Fact]
    public async Task Manager_ClosedWindow_DoesNotStart_ThenStartsWhenOpen()
    {
        await using var s = TestServer.Start(20_000);
        var (m, dir) = Make(Q(TimeSpan.FromHours(23), TimeSpan.FromHours(6)));
        var now = At(12);
        m.Clock = () => now;
        var id = m.Add(Req(s, dir));
        await Task.Delay(300);
        Assert.Equal(DownloadState.Queued, m.Get(id)!.State);
        now = At(23, 30);
        m.Tick();
        await WaitFor(m, id, r => r.State == DownloadState.Completed);
    }

    [Fact]
    public async Task Manager_WindowCloses_RequeuesRunningDownload()
    {
        await using var s = TestServer.Start(600_000);
        s.SlowFromOffset = 0; s.SlowDelayMs = 60;
        var (m, dir) = Make(Q(TimeSpan.FromHours(9), TimeSpan.FromHours(17)));
        var now = At(10);
        m.Clock = () => now;
        var id = m.Add(Req(s, dir));
        await WaitFor(m, id, r => r.State == DownloadState.Running && r.Downloaded > 0);
        now = At(18);
        m.Tick();
        await WaitFor(m, id, r => r.State == DownloadState.Queued);
        now = At(10);
        m.Tick();
        var done = await WaitFor(m, id, r => r.State == DownloadState.Completed);
        Assert.Equal(s.Body, await File.ReadAllBytesAsync(done.FilePath!));
    }

    [Fact]
    public async Task Manager_QueueFinished_RunsPostActionOnce()
    {
        await using var s = TestServer.Start(20_000);
        var (m, dir) = Make(Q(null, null, PostAction.Shutdown));
        var power = new FakePower();
        m.PowerApi = power;
        var a = m.Add(Req(s, Path.Combine(dir, "a")));
        var b = m.Add(Req(s, Path.Combine(dir, "b")));
        await WaitFor(m, a, r => r.State == DownloadState.Completed);
        await WaitFor(m, b, r => r.State == DownloadState.Completed);
        await Task.Delay(300);
        Assert.Equal(new[] { "shutdown" }, power.Calls);
    }

    [Fact]
    public async Task Manager_QueueFinished_ConcurrentCompletions_FirePostActionExactlyOnce()
    {
        await using var s = TestServer.Start(2_000);
        for (var round = 0; round < 25; round++)
        {
            var (m, dir) = Make(Q(null, null, PostAction.Shutdown));
            var power = new FakePower();
            m.PowerApi = power;
            var ids = Enumerable.Range(0, 4).Select(i => m.Add(Req(s, Path.Combine(dir, $"d{i}")))).ToList();
            foreach (var id in ids) await WaitFor(m, id, r => r.State == DownloadState.Completed);
            await Task.Delay(60);
            Assert.True(power.Calls.Count == 1, $"round {round}: post-action ran {power.Calls.Count} times");
        }
    }
}
