using System.Text;
using BDM.Core.Data;
using BDM.Core.Engine;
using BDM.Core.Ext;
using BDM.Core.Files;
using BDM.Core.Http;
using BDM.Core.Streams;

namespace BDM.Core.Tests;

/// <summary>Regression tests for the findings of the independent whole-branch review.</summary>
public class ReviewFixTests
{
    static readonly HttpClient Shared = new();
    static string Tmp() => Directory.CreateTempSubdirectory().FullName;

    static (DownloadManager M, DownloadStore S) Make(int connections = 2)
    {
        var store = new DownloadStore(Path.Combine(Tmp(), "bdm.db"));
        store.SaveQueue(new QueueDefinition("main", "Main", 3, connections, null, null, PostAction.None));
        return (new DownloadManager(store, _ => Shared) { BaseOptions = new DownloadOptions(RetryBaseDelay: TimeSpan.FromMilliseconds(10)) }, store);
    }

    static DownloadRequest Req(Uri url, string dir, string? cookie = null, Dictionary<string, string>? headers = null) =>
        new(url, null, null, headers ?? new Dictionary<string, string>(), cookie, null, null, dir);

    static async Task<DownloadRecord> WaitFor(DownloadManager m, Guid id, Func<DownloadRecord, bool> cond, int seconds = 20)
    {
        var end = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < end) { var r = m.Get(id); if (r != null && cond(r)) return r; await Task.Delay(20); }
        throw new TimeoutException($"state={m.Get(id)?.State} err={m.Get(id)?.Error}");
    }

    static async Task Eventually(Func<bool> cond, string what, int seconds = 15)
    {
        var end = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < end) { if (cond()) return; await Task.Delay(25); }
        Assert.Fail("timed out waiting for " + what);
    }

    // ---- Critical: removing an unfinished download must never delete someone else's file ----
    [Fact]
    public async Task Remove_UnfinishedWithDeleteFiles_NeverTouchesAnExistingFileWithTheSameName()
    {
        await using var s = TestServer.Start(600_000);
        s.SlowFromOffset = 0; s.SlowDelayMs = 60;
        var dir = Tmp();
        var existing = Path.Combine(dir, "file");
        File.WriteAllText(existing, "precious");
        var (m, _) = Make();
        var id = m.Add(Req(s.FileUrl, dir));
        await WaitFor(m, id, r => r.State == DownloadState.Running && r.Downloaded > 0);
        m.Pause(id);
        await WaitFor(m, id, r => r.State == DownloadState.Paused);
        await Task.Delay(600); // the cancelled run finishes unwinding and saves its sidecar

        m.Remove(id, deleteFile: true);

        Assert.Equal("precious", File.ReadAllText(existing));
        Assert.Empty(Directory.GetFiles(dir, "*.bdmpart*"));
    }

    [Fact]
    public async Task Remove_RunningDownload_WithDeleteFiles_CleansUpPartFilesOnceItStops()
    {
        await using var s = TestServer.Start(600_000);
        s.SlowFromOffset = 0; s.SlowDelayMs = 60;
        var dir = Tmp();
        var (m, _) = Make();
        var id = m.Add(Req(s.FileUrl, dir));
        await WaitFor(m, id, r => r.State == DownloadState.Running && r.Downloaded > 0);

        m.Remove(id, deleteFile: true);

        await Eventually(() => Directory.GetFiles(dir, "*.bdmpart*").Length == 0, "the part files of the removed running download to disappear");
    }

    // ---- Important: same-named downloads in one folder must not collide ----
    [Fact]
    public async Task TwoDownloadsWithTheSameName_InOneFolder_BothComplete_UnderDifferentNames()
    {
        await using var s = TestServer.Start(400_000);
        var dir = Tmp();
        var (m, _) = Make();
        var a = m.Add(Req(s.FileUrl, dir));
        var b = m.Add(Req(s.FileUrl, dir));
        var da = await WaitFor(m, a, r => r.State is DownloadState.Completed or DownloadState.Failed);
        var db = await WaitFor(m, b, r => r.State is DownloadState.Completed or DownloadState.Failed);
        Assert.True(da.State == DownloadState.Completed, da.Error);
        Assert.True(db.State == DownloadState.Completed, db.Error);
        Assert.NotEqual(da.FilePath, db.FilePath);
        Assert.Equal(s.Body, await File.ReadAllBytesAsync(da.FilePath!));
        Assert.Equal(s.Body, await File.ReadAllBytesAsync(db.FilePath!));
    }

    [Fact]
    public async Task PausedDownload_KeepsItsProgress_WhenAnotherSameNamedDownloadRuns()
    {
        await using var s = TestServer.Start(600_000);
        s.SlowFromOffset = 0; s.SlowDelayMs = 60;
        var dir = Tmp();
        var (m, _) = Make();
        var a = m.Add(Req(s.FileUrl, dir));
        await WaitFor(m, a, r => r.State == DownloadState.Running && r.Downloaded > 0);
        m.Pause(a);
        await WaitFor(m, a, r => r.State == DownloadState.Paused);
        await Task.Delay(600);
        Assert.Single(Directory.GetFiles(dir, "*.bdmpart.json"));

        s.SlowFromOffset = long.MaxValue; // the next one is quick
        var b = m.Add(Req(s.FileUrl, dir));
        await WaitFor(m, b, r => r.State == DownloadState.Completed);
        Assert.Single(Directory.GetFiles(dir, "*.bdmpart.json")); // A's sidecar survived B

        m.Resume(a);
        var done = await WaitFor(m, a, r => r.State == DownloadState.Completed);
        Assert.Equal(s.Body, await File.ReadAllBytesAsync(done.FilePath!));
    }

    // ---- Important: pause + quick resume must not start the same download twice ----
    sealed class GatedYtDlp : IYtDlpProcess
    {
        public List<TaskCompletionSource<bool>> Gates { get; } = new();
        public int Calls { get { lock (Gates) return Gates.Count; } }
        public async Task<(int Exit, string StdOut, string StdErr)> RunAsync(IReadOnlyList<string> args, Action<string>? onLine, CancellationToken ct)
        {
            TaskCompletionSource<bool> gate;
            lock (Gates) { gate = new(TaskCreationOptions.RunContinuationsAsynchronously); Gates.Add(gate); }
            if (await gate.Task) throw new OperationCanceledException(); // true = this run was cancelled
            var dir = Path.GetDirectoryName(args[args.ToList().IndexOf("-o") + 1])!;
            var file = Path.Combine(dir, "Clip.mp4");
            File.WriteAllBytes(file, new byte[1024]);
            onLine?.Invoke("BDMFILE " + file);
            return (0, "", "");
        }
    }

    static StreamChoice YtChoice(Uri page) => new("Best", new StreamTrack("b", page), null, []);

    [Fact]
    public async Task PauseThenQuickResume_DoesNotStartASecondRunWhileTheFirstStillUnwinds()
    {
        var (m, _) = Make();
        var gated = new GatedYtDlp();
        m.YtDlp = new YtDlpRunner(() => "yt-dlp.exe", _ => gated);
        var page = new Uri("https://v.example/watch");
        var id = m.AddYtDlp(page, YtChoice(page), Tmp(), null, null);
        await Eventually(() => gated.Calls == 1, "the first run to start");

        m.Pause(id);
        m.Resume(id);
        await Task.Delay(400);
        Assert.Equal(1, gated.Calls); // the resumed run must wait for the first to finish unwinding

        gated.Gates[0].SetResult(true); // the first run ends by cancelling
        await Eventually(() => gated.Calls == 2, "the resumed run to start after the first one ended");
        gated.Gates[1].SetResult(false); // the second run completes
        var done = await WaitFor(m, id, r => r.State == DownloadState.Completed);
        Assert.EndsWith("Clip.mp4", done.FilePath);
    }

    // ---- Important: errors that are not network problems must not be retried as network problems ----
    sealed class LambdaYt(Func<(int Exit, string Err)> behave, Exception? throws = null) : IYtDlpProcess
    {
        public Task<(int Exit, string StdOut, string StdErr)> RunAsync(IReadOnlyList<string> args, Action<string>? onLine, CancellationToken ct)
        {
            if (throws != null) throw throws;
            var (exit, err) = behave();
            return Task.FromResult((exit, "", err));
        }
    }

    [Fact]
    public async Task NonNetworkFailure_IsMarkedOther_AndNotRetriedWhenTheNetworkComesBack()
    {
        var (m, _) = Make();
        m.YtDlp = new YtDlpRunner(() => "yt-dlp.exe", _ => new LambdaYt(() => (1, "ERROR: Unsupported URL")));
        var page = new Uri("https://v.example/w");
        var id = m.AddYtDlp(page, YtChoice(page), Tmp(), null, null);
        var failed = await WaitFor(m, id, r => r.State == DownloadState.Failed);
        Assert.Equal(DownloadFailure.Other, failed.Failure);

        m.ResumeNetworkFailures();
        await Task.Delay(300);
        Assert.Equal(DownloadState.Failed, m.Get(id)!.State);
    }

    [Fact]
    public async Task DiskFullDuringAJob_IsReportedAsDiskFull()
    {
        var (m, _) = Make();
        m.YtDlp = new YtDlpRunner(() => "yt-dlp.exe", _ => new LambdaYt(() => (0, ""), new IOException("disk", unchecked((int)0x80070070))));
        var page = new Uri("https://v.example/w");
        var id = m.AddYtDlp(page, YtChoice(page), Tmp(), null, null);
        var failed = await WaitFor(m, id, r => r.State == DownloadState.Failed);
        Assert.Equal(DownloadFailure.DiskFull, failed.Failure);
    }

    // ---- Important: session secrets must not live in the database forever or travel in exports ----
    [Fact]
    public async Task CompletedDownloads_NoLongerKeepSessionSecrets()
    {
        await using var s = TestServer.Start(30_000);
        var (m, store) = Make();
        var id = m.Add(Req(s.FileUrl, Tmp(), cookie: "sessionid=SECRET", headers: new() { ["Authorization"] = "Basic SECRET2", ["X-Keep"] = "1" }));
        var done = await WaitFor(m, id, r => r.State == DownloadState.Completed);
        foreach (var rec in new[] { done, store.Get(id)! })
        {
            Assert.Null(rec.Request.CookieHeader);
            Assert.False(rec.Request.Headers.ContainsKey("Authorization"));
            Assert.Equal("1", rec.Request.Headers["X-Keep"]);
        }
    }

    [Fact]
    public void Export_NeverContainsCookiesOrAuthorization_EvenForUnfinishedDownloads()
    {
        var store = new DownloadStore(Path.Combine(Tmp(), "bdm.db"));
        var req = new DownloadRequest(new Uri("https://x/a.zip"), null, null, new Dictionary<string, string> { ["Authorization"] = "Basic SECRET2", ["Cookie"] = "c=SECRET3" }, "sessionid=SECRET", null, null, ".");
        store.Upsert(new DownloadRecord(Guid.NewGuid(), req, DownloadState.Queued, 0, null, null, "main", DateTime.UtcNow,
            Stream: new StreamSource(new Uri("https://x/m.m3u8"), StreamKind.Hls, new Dictionary<string, string> { ["Authorization"] = "Bearer SECRET4" }, "sid=SECRET5", "t", null)));
        var json = store.Export();
        Assert.DoesNotContain("SECRET", json);
        Assert.Contains("https://x/a.zip", json);
    }

    // ---- Important: finished files carry the browser's Mark-of-the-Web ----
    [Fact]
    public void ZoneIdentifier_MarksAFileAsFromTheInternet()
    {
        var path = Path.Combine(Tmp(), "setup.exe");
        File.WriteAllText(path, "x");
        Assert.True(ZoneIdentifier.Mark(path, new Uri("https://dl.example/setup.exe"), "https://dl.example/page"));
        var text = File.ReadAllText(path + ":Zone.Identifier");
        Assert.Contains("ZoneId=3", text);
        Assert.Contains("HostUrl=https://dl.example/setup.exe", text);
        Assert.Contains("ReferrerUrl=https://dl.example/page", text);
        Assert.Equal("x", File.ReadAllText(path)); // the file itself is untouched
        Assert.False(ZoneIdentifier.Mark(Path.Combine(Tmp(), "missing.bin"), null, null)); // never throws
    }

    [Fact]
    public async Task EveryFinishedDownload_GetsMarked()
    {
        await using var s = TestServer.Start(30_000);
        var (m, _) = Make();
        var id = m.Add(Req(s.FileUrl, Tmp()));
        var done = await WaitFor(m, id, r => r.State == DownloadState.Completed);
        Assert.Contains("ZoneId=3", File.ReadAllText(done.FilePath + ":Zone.Identifier"));

        var gated = new GatedYtDlp();
        m.YtDlp = new YtDlpRunner(() => "yt-dlp.exe", _ => gated);
        var page = new Uri("https://v.example/w");
        var yt = m.AddYtDlp(page, YtChoice(page), Tmp(), null, null);
        await Eventually(() => gated.Calls == 1, "the yt-dlp job to start");
        gated.Gates[0].SetResult(false); // finish normally
        var ytDone = await WaitFor(m, yt, r => r.State == DownloadState.Completed);
        Assert.Contains("ZoneId=3", File.ReadAllText(ytDone.FilePath + ":Zone.Identifier"));
    }

    [Fact]
    public void FileNames_LoseBidirectionalControlCharacters()
    {
        // "invoice<RLO>fdp.exe" would be displayed as "invoiceexe.pdf"
        Assert.Equal("invoicefdp.exe", FileNaming.Sanitize("invoice‮fdp.exe"));
        Assert.Equal("ab.txt", FileNaming.Sanitize("a⁦b⁩.txt"));
        Assert.Equal("é.txt", FileNaming.Sanitize("é.txt"));
    }

    // ---- Important: a fresh link for a failed (expired) download continues that download ----
    [Fact]
    public async Task Add_ForAnExpiredLinkOfAFailedDownload_RefreshesThatDownload_AndKeepsItsProgress()
    {
        await using var s = TestServer.Start(1_000_000);
        s.ExpireAfterBytes = 300_000;
        var dir = Tmp();
        var (m, _) = Make();
        var old = new Uri(s.FileUrl + "?token=1&id=7");
        var id = m.Add(Req(old, dir));
        var failed = await WaitFor(m, id, r => r.State == DownloadState.Failed);
        Assert.Equal(DownloadFailure.AuthExpired, failed.Failure);

        s.ResetExpiry(); // the owner reopened the page and the extension sends the new signed link
        var servedBefore = s.BytesServed;
        var again = m.Add(Req(new Uri(s.FileUrl + "?token=2&id=7"), dir));

        Assert.Equal(id, again);
        Assert.Single(m.All());
        var done = await WaitFor(m, id, r => r.State == DownloadState.Completed);
        Assert.Equal(s.Body, await File.ReadAllBytesAsync(done.FilePath!));
        Assert.Contains("token=2", done.Request.Url.ToString());
        Assert.True(s.BytesServed - servedBefore < 1_000_000, "the fresh link should only fetch what was missing");
    }

    [Fact]
    public async Task Add_ForADifferentFile_IsNotMistakenForARefresh()
    {
        await using var s = TestServer.Start(30_000);
        s.ExpireAfterBytes = 1;
        var dir = Tmp();
        var (m, _) = Make();
        var id = m.Add(Req(new Uri(s.FileUrl + "?id=1"), dir));
        await WaitFor(m, id, r => r.State == DownloadState.Failed);
        var other = m.Add(Req(new Uri(s.FileUrl + "?id=2"), dir));
        Assert.NotEqual(id, other);
    }

    [Fact]
    public void UrlKey_IgnoresSignatureParametersOnly()
    {
        Assert.Equal(UrlKey.Normalize(new Uri("https://x/a.mp4?token=1&id=5#f")), UrlKey.Normalize(new Uri("https://x/a.mp4?id=5&token=2")));
        Assert.NotEqual(UrlKey.Normalize(new Uri("https://x/a.mp4?id=5")), UrlKey.Normalize(new Uri("https://x/a.mp4?id=6")));
        Assert.Equal(UrlKey.Normalize(new Uri("https://x/a.mp4?X-Amz-Signature=a&X-Amz-Date=b")), UrlKey.Normalize(new Uri("https://x/a.mp4")));
    }

    // ---- Important: rolling text log ----
    [Fact]
    public void Log_WritesDatedFiles_AndPrunesOldOnes()
    {
        var dir = Tmp();
        var old = Path.Combine(dir, "bdm-2020-01-01.log");
        File.WriteAllText(old, "ancient");
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-30));
        try
        {
            Log.Configure(dir);
            Log.Info("hello-from-test-7731");
            Log.Error("bad-thing-7731", new InvalidOperationException("boom-7731"));
            var file = Assert.Single(Directory.GetFiles(dir, "bdm-*.log"), f => !f.EndsWith("2020-01-01.log"));
            var text = File.ReadAllText(file);
            Assert.Contains("hello-from-test-7731", text);
            Assert.Contains("bad-thing-7731", text);
            Assert.Contains("InvalidOperationException", text);
            Assert.Contains("boom-7731", text);
            Assert.False(File.Exists(old), "logs older than a week are pruned");
        }
        finally { Log.Configure(null); }
        Log.Info("not configured: must be a harmless no-op");
    }

    [Fact]
    public async Task FailedDownloads_AreLogged()
    {
        var dir = Tmp();
        try
        {
            Log.Configure(dir);
            var (m, _) = Make();
            m.YtDlp = new YtDlpRunner(() => "yt-dlp.exe", _ => new LambdaYt(() => (1, "ERROR: Unsupported URL log-marker-4482")));
            var page = new Uri("https://v.example/w");
            var id = m.AddYtDlp(page, YtChoice(page), Tmp(), null, null);
            await WaitFor(m, id, r => r.State == DownloadState.Failed);
            await Eventually(() => Directory.GetFiles(dir, "bdm-*.log").Any(f => File.ReadAllText(f).Contains("log-marker-4482")), "the failure to reach the log");
        }
        finally { Log.Configure(null); }
    }
}
