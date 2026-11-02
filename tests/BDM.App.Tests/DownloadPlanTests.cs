using BDM.App.Services;
using BDM.App.Settings;
using BDM.App.ViewModels;
using BDM.Core.Data;
using BDM.Core.Engine;
using BDM.Core.Http;
using BDM.Core.Ipc;
using BDM.Core.Net;

namespace BDM.App.Tests;

/// <summary>The IDM-style "download file info" step: every new download can be named, filed and described before it starts.</summary>
public class DownloadPlanTests
{
    static (DownloadManager M, AppSettings S) Engine()
    {
        var store = new DownloadStore(Path.Combine(Directory.CreateTempSubdirectory().FullName, "bdm.db"));
        store.SaveQueue(new QueueDefinition("main", "Main", 0, 4, null, null, PostAction.None)); // nothing runs
        return (new DownloadManager(store, _ => new HttpClient()), new AppSettings { DownloadRoot = Path.Combine(Path.GetTempPath(), "bdm-plan") });
    }

    sealed class Prompt(Func<DownloadPlan, DownloadPlan?> answer) : IDownloadPrompt
    {
        public DownloadPlan? Proposed;
        public DownloadRequest? Template;
        public Task<DownloadPlan?> AskAsync(DownloadPlan proposal, DownloadRequest template)
        {
            Proposed = proposal;
            Template = template;
            return Task.FromResult(answer(proposal));
        }
    }

    static HostMessage File(string url) =>
        new("add", url, "https://site.example/watch", "Clip", new() { ["Referer"] = "https://player.example/", ["User-Agent"] = "UA" }, "sid=1", null, null);

    [Fact]
    public void Propose_NamesFilesAndFolderFromTheAddress()
    {
        var (_, s) = Engine();
        var p = DownloadPlan.Propose(new Uri("https://x.example/a/movie.mkv?sig=1"), s);
        Assert.Equal("movie.mkv", p.SuggestedName);
        Assert.Null(p.FileName); // automatic until the owner types a name
        Assert.Equal("Video", p.Category);
        Assert.Equal(Path.Combine(s.DownloadRoot, "Video"), p.Folder);
        Assert.True(p.StartNow);
    }

    [Fact]
    public void Remember_SetsTheCategoryFolder()
    {
        var (_, s) = Engine();
        Categories.Remember(s, "Video", @"D:\Films");
        Assert.Equal(@"D:\Films", Categories.FolderFor(s, "Video"));
        Categories.Remember(s, "Something odd", @"D:\Misc"); // unknown categories file under Other
        Assert.Equal(@"D:\Misc", Categories.FolderFor(s, "Other"));
    }

    [Fact]
    public void ViewModelAdd_UsesTheOwnersNameFolderCategoryAndDescription()
    {
        var (m, s) = Engine();
        var vm = new MainViewModel(m, s, new NoShell());
        var plan = DownloadPlan.Propose(new Uri("https://x.example/file.mp4"), s) with
        {
            FileName = "Holiday.mp4", Folder = @"D:\Films", Category = "Video", Description = "Summer", StartNow = false,
        };
        var id = vm.Add(plan);
        var r = m.Get(id)!;
        Assert.Equal("Holiday.mp4", r.Request.FileName);
        Assert.Equal(@"D:\Films", r.Request.DestinationDir);
        Assert.Equal("Summer", r.Request.Description);
        Assert.Equal(DownloadState.Paused, r.State); // "Download later"
        Assert.Equal("Summer", Assert.Single(vm.Items).Description);
    }

    [Fact]
    public async Task BrowserDownload_AsksFirst_AndKeepsTheBrowserHeaders()
    {
        var (m, s) = Engine();
        var prompt = new Prompt(p => p with { FileName = "Mine.mp4", Description = "from the site" });
        var h = new HostMessageHandler(m, s, new NoPicker(), prompt);
        Guid? started = null;
        h.Started += id => started = id;

        Assert.True((await h.HandleAsync(File("https://cdn.example/file.mp4?sign=1"))).Ok);

        Assert.Equal("file.mp4", prompt.Proposed!.SuggestedName);
        Assert.Equal("https://player.example/", prompt.Template!.Referer); // the dialog probes with the browser's headers
        var r = Assert.Single(m.All());
        Assert.Equal("Mine.mp4", r.Request.FileName);
        Assert.Equal("from the site", r.Request.Description);
        Assert.Equal("UA", r.Request.Headers["User-Agent"]);
        Assert.Equal("sid=1", r.Request.CookieHeader);
        Assert.Equal(r.Id, started); // the app opens its progress window for it
    }

    [Fact]
    public async Task TheConnectionsChosenInTheDialog_GoWithTheDownload()
    {
        var (m, s) = Engine();
        var h = new HostMessageHandler(m, s, new NoPicker(), new Prompt(p => p with { Connections = 1 }));
        Assert.True((await h.HandleAsync(File("https://fs45.hotlink.example/x/movie.mp4"))).Ok);
        Assert.Equal(1, Assert.Single(m.All()).Request.Connections);
        Assert.Null(DownloadPlan.Propose(new Uri("https://x.example/a.zip"), s).Connections); // the queue's setting unless chosen
    }

    [Fact]
    public async Task AVideoFromThePage_IsNamedAfterThePageTitle_KeepingItsFileType()
    {
        // IDM names a caught video after the page, not the CDN's number ("1736412345.mp4").
        var (m, s) = Engine();
        var prompt = new Prompt(p => p);
        var h = new HostMessageHandler(m, s, new NoPicker(), prompt);
        Assert.True((await h.HandleAsync(File("https://cdn.example/v/1736412345.mp4?sign=1") with { Title = "My Holiday: Part 1", Kind = "video" })).Ok);
        Assert.Equal("My Holiday_ Part 1.mp4", prompt.Proposed!.FileName);
        Assert.Equal("Video", prompt.Proposed.Category);
        Assert.Equal("My Holiday_ Part 1.mp4", Assert.Single(m.All()).Request.FileName);
    }

    [Theory]
    [InlineData("https://cdn.example/play?id=7", "video", "Clip.mp4")] // no file type in the address
    [InlineData("https://cdn.example/a/stream.php", "audio", "Clip.m4a")] // not a media file type
    [InlineData("https://cdn.example/a/9.webm", "video", "Clip.webm")]
    public void Propose_ATitledVideo_GetsAMediaFileType(string url, string kind, string expected) =>
        Assert.Equal(expected, DownloadPlan.Propose(new Uri(url), Engine().S, "Clip", kind).FileName);

    [Fact]
    public async Task AFileLink_KeepsItsOwnName_EvenThoughThePageHasATitle()
    {
        var (m, s) = Engine();
        var prompt = new Prompt(p => p);
        var h = new HostMessageHandler(m, s, new NoPicker(), prompt);
        Assert.True((await h.HandleAsync(File("https://cdn.example/setup.exe") with { Kind = "file" })).Ok);
        Assert.Null(prompt.Proposed!.FileName);
        Assert.Equal("setup.exe", prompt.Proposed.SuggestedName);
    }

    [Fact]
    public async Task BrowserDownload_CancelledInTheDialog_AddsNothing_ButStillAnswersOk()
    {
        // Ok, so the extension cancels the browser's own copy too: Cancel means "don't download this".
        var (m, s) = Engine();
        var h = new HostMessageHandler(m, s, new NoPicker(), new Prompt(_ => null));
        Assert.True((await h.HandleAsync(File("https://cdn.example/file.mp4"))).Ok);
        Assert.Empty(m.All());
    }

    [Fact]
    public async Task BrowserDownload_WithTheDialogTurnedOff_StartsStraightAway()
    {
        var (m, s) = Engine();
        s.ShowDownloadDialog = false;
        var prompt = new Prompt(p => p);
        var h = new HostMessageHandler(m, s, new NoPicker(), prompt);
        Assert.True((await h.HandleAsync(File("https://cdn.example/file.mp4"))).Ok);
        Assert.Null(prompt.Proposed);
        Assert.Single(m.All());
    }

    [Theory]
    [InlineData(403)]
    [InlineData(503)]
    public async Task BrowserDownload_TheSiteRefusesBdm_HandsItBackToTheBrowser(int status)
    {
        // Cloudflare-protected video CDNs refuse anything but a real browser (seen on vdcdn.xyz): let the browser fetch it.
        var (m, s) = Engine();
        var prompt = new Prompt(p => p);
        DownloadRequest? probed = null;
        var h = new HostMessageHandler(m, s, new NoPicker(), prompt, req =>
        {
            probed = req;
            return status == 403
                ? throw new DownloadException(DownloadFailure.AuthExpired, "refused")
                : throw new HttpRequestException("busy", null, (System.Net.HttpStatusCode)status);
        });

        var reply = await h.HandleAsync(File("https://cdn.example/file.mp4"));

        Assert.False(reply.Ok);
        Assert.True(reply.UseBrowser);
        Assert.Equal("UA", probed!.Headers["User-Agent"]); // probed as the browser would ask
        Assert.Null(prompt.Proposed); // no dialog for a download BDM can't do
        Assert.Empty(m.All());
    }

    static readonly RelayEndpoint Relay = new(4321, "secret");

    [Fact]
    public async Task BrowserDownload_TheSiteRefusesBdm_GoesThroughTheBrowserRelay_WhenTheExtensionCanDoIt()
    {
        var (m, s) = Engine();
        var prompt = new Prompt(p => p with { Description = "via chrome" });
        var h = new HostMessageHandler(m, s, new NoPicker(), prompt, _ => throw new DownloadException(DownloadFailure.AuthExpired, "refused"), Relay);

        var reply = await h.HandleAsync(File("https://cdn.example/file.mp4") with { CanRelay = true });

        Assert.True(reply.Ok);
        Assert.Equal((4321, "secret"), (reply.RelayPort, reply.RelayToken)); // the extension starts serving the relay
        Assert.NotNull(prompt.Proposed); // still the owner's dialog
        var r = Assert.Single(m.All());
        Assert.Equal(BrowserRelay.ProfileId, r.Request.ProxyProfileId);
        Assert.Equal("via chrome", r.Request.Description);
    }

    [Fact]
    public async Task BrowserDownload_AnOlderExtension_StillGetsTheBrowserFallback()
    {
        var (m, s) = Engine();
        var h = new HostMessageHandler(m, s, new NoPicker(), null, _ => throw new DownloadException(DownloadFailure.AuthExpired, "refused"), Relay);
        var reply = await h.HandleAsync(File("https://cdn.example/file.mp4")); // no CanRelay
        Assert.True(reply.UseBrowser);
        Assert.Empty(m.All());
    }

    [Fact]
    public async Task RelayInfo_TellsTheExtensionWhereTheRelayIs()
    {
        var (m, s) = Engine();
        var on = await new HostMessageHandler(m, s, new NoPicker(), null, null, Relay).HandleAsync(new HostMessage("relayInfo", null, null, null, null, null, null, null));
        Assert.Equal((true, 4321, "secret"), (on.Ok, on.RelayPort, on.RelayToken));
        var off = await new HostMessageHandler(m, s, new NoPicker()).HandleAsync(new HostMessage("relayInfo", null, null, null, null, null, null, null));
        Assert.False(off.Ok);
    }

    [Fact]
    public async Task BrowserDownload_AProbeThatJustTimesOut_StillQueuesIt()
    {
        var (m, s) = Engine();
        var h = new HostMessageHandler(m, s, new NoPicker(), new Prompt(p => p), _ => throw new TaskCanceledException());
        Assert.True((await h.HandleAsync(File("https://cdn.example/file.mp4"))).Ok);
        Assert.Single(m.All());
    }

    [Fact]
    public void ClipboardLink_AcceptsAnyWebOrFtpAddress_ForPastingIntoAddUrl()
    {
        Assert.Equal("https://x.example/page", DownloadPlan.LinkFrom("  https://x.example/page \r\n")!.ToString());
        Assert.NotNull(DownloadPlan.LinkFrom("ftp://files.example/a.iso"));
        Assert.Null(DownloadPlan.LinkFrom("hello world"));
        Assert.Null(DownloadPlan.LinkFrom("file:///c:/x.zip"));
        Assert.Null(DownloadPlan.LinkFrom(null));
    }

    sealed class NoShell : IShell { public void Open(string path) { } public void ShowInFolder(string path) { } }
    sealed class NoPicker : IQualityPicker
    {
        public Task<BDM.Core.Streams.StreamChoice?> PickAsync(IReadOnlyList<BDM.Core.Streams.StreamChoice> choices, string title) => Task.FromResult<BDM.Core.Streams.StreamChoice?>(null);
    }
}
