using BDM.App.Services;
using BDM.App.Settings;
using BDM.Core.Data;
using BDM.Core.Engine;
using BDM.Core.Ext;
using BDM.Core.Ipc;
using BDM.Core.Streams;

namespace BDM.App.Tests;

public class YtDlpHandlerTests
{
    const string InfoJson = """
        {"title":"My Video","is_live":false,"formats":[
          {"format_id":"140","vcodec":"none","acodec":"mp4a.40.2"},
          {"format_id":"136","vcodec":"avc1","acodec":"none","height":720},
          {"format_id":"137","vcodec":"avc1","acodec":"none","height":1080}]}
        """;

    sealed class FakeProc(int exit = 0, string stdout = InfoJson, string stderr = "") : IYtDlpProcess
    {
        public List<IReadOnlyList<string>> Calls { get; } = new();
        public Task<(int Exit, string StdOut, string StdErr)> RunAsync(IReadOnlyList<string> args, Action<string>? onLine, CancellationToken ct)
        {
            Calls.Add(args);
            return Task.FromResult((exit, stdout, stderr));
        }
    }

    sealed class Picker(int? pick) : IQualityPicker
    {
        public int Asked;
        public IReadOnlyList<StreamChoice>? Offered;
        public Task<StreamChoice?> PickAsync(IReadOnlyList<StreamChoice> choices, string title)
        {
            Asked++;
            Offered = choices;
            return Task.FromResult(pick is { } i ? choices[i] : null);
        }
    }

    static (HostMessageHandler H, DownloadManager M, Picker P, FakeProc Proc) Make(FakeProc? proc = null, int? pick = 0, bool ytDlpConfigured = true, bool alwaysAsk = false)
    {
        var store = new DownloadStore(Path.Combine(Directory.CreateTempSubdirectory().FullName, "bdm.db"));
        store.SaveQueue(new QueueDefinition("main", "Main", 0, 4, null, null, PostAction.None)); // nothing runs
        var m = new DownloadManager(store, _ => new HttpClient());
        proc ??= new FakeProc();
        if (ytDlpConfigured) m.YtDlp = new YtDlpRunner(() => "yt-dlp.exe", _ => proc);
        var picker = new Picker(pick);
        var settings = new AppSettings { DownloadRoot = Directory.CreateTempSubdirectory().FullName, AlwaysAskQuality = alwaysAsk };
        return (new HostMessageHandler(m, settings, picker), m, picker, proc);
    }

    static HostMessage Page(string op) => new(op, "https://video.example/watch?v=1", "https://video.example/watch?v=1", "A page", new() { ["Referer"] = "https://video.example/" }, "a=b", null, null);

    [Fact]
    public async Task AddPage_UsesBestQualityByDefault()
    {
        var (h, m, picker, proc) = Make();
        Assert.True((await h.HandleAsync(Page("addPage"))).Ok);
        Assert.Equal(0, picker.Asked);
        var rec = Assert.Single(m.All());
        Assert.Equal("bv*[height<=1080]+ba/b[height<=1080]", rec.YtDlpFormat);
        Assert.Equal("https://video.example/watch?v=1", rec.Request.Url.ToString());
        Assert.Equal("a=b", rec.Request.CookieHeader);
        Assert.Equal("https://video.example/", rec.Request.Referer);
        Assert.EndsWith("Video", rec.Request.DestinationDir);
        Assert.Contains("Cookie: a=b", proc.Calls.Single()); // the page was inspected with the browser's cookie
    }

    [Fact]
    public async Task AddPageAsk_ShowsThePickerAndHonoursTheChoice()
    {
        var (h, m, picker, _) = Make(pick: 1);
        Assert.True((await h.HandleAsync(Page("addPageAsk"))).Ok);
        Assert.Equal(1, picker.Asked);
        Assert.Equal(new[] { "1080p", "720p", "Audio only" }, picker.Offered!.Select(c => c.Label));
        Assert.Equal("bv*[height<=720]+ba/b[height<=720]", m.All()[0].YtDlpFormat);
    }

    [Fact]
    public async Task AddPage_AlwaysAskSetting_AsksOnPlainAddPage()
    {
        var (h, _, picker, _) = Make(alwaysAsk: true);
        Assert.True((await h.HandleAsync(Page("addPage"))).Ok);
        Assert.Equal(1, picker.Asked);
    }

    [Fact]
    public async Task AddPage_PickerCancelled_AddsNothing()
    {
        var (h, m, _, _) = Make(pick: null);
        var r = await h.HandleAsync(Page("addPageAsk"));
        Assert.False(r.Ok);
        Assert.Contains("Cancelled", r.Error);
        Assert.Empty(m.All());
    }

    [Fact]
    public async Task AddPage_YtDlpNotSetUp_ExplainsWhereToFixIt()
    {
        var (h, m, _, _) = Make(ytDlpConfigured: false);
        var r = await h.HandleAsync(Page("addPage"));
        Assert.False(r.Ok);
        Assert.Contains("yt-dlp", r.Error);
        Assert.Contains("Options", r.Error);
        Assert.Empty(m.All());
    }

    [Fact]
    public async Task AddPage_YtDlpError_IsPassedOnReadably()
    {
        var (h, m, _, _) = Make(new FakeProc(exit: 1, stdout: "", stderr: "ERROR: [generic] Unsupported URL"));
        var r = await h.HandleAsync(Page("addPage"));
        Assert.False(r.Ok);
        Assert.Contains("Unsupported URL", r.Error);
        Assert.Empty(m.All());
    }

    [Fact]
    public async Task AddPage_BadAddress_IsRefused()
    {
        var (h, m, _, proc) = Make();
        Assert.False((await h.HandleAsync(new HostMessage("addPage", "not a url", null, null, null, null, null, null))).Ok);
        Assert.False((await h.HandleAsync(new HostMessage("addPage", "file:///c:/x", null, null, null, null, null, null))).Ok);
        Assert.Empty(m.All());
        Assert.Empty(proc.Calls);
    }

    [Fact]
    public async Task AddPage_AppliesTheRoutedProxyToYtDlp()
    {
        var (h, m, _, proc) = Make();
        m.GlobalProxyId = "p1";
        m.ProxyUrlFor = id => id == "p1" ? "socks5://u:pw@proxy:1080" : null;
        await h.HandleAsync(Page("addPage"));
        var args = proc.Calls.Single();
        Assert.Equal("socks5://u:pw@proxy:1080", args[args.ToList().IndexOf("--proxy") + 1]);
    }
}
