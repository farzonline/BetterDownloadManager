using System.IO.Pipes;
using BDM.App.Services;
using BDM.App.Settings;
using BDM.Core.Data;
using BDM.Core.Engine;
using BDM.Core.Ipc;
using BDM.Core.Streams;

namespace BDM.App.Tests;

public class AppLogicTests
{
    static string Tmp() => Directory.CreateTempSubdirectory().FullName;

    // ---- categories ----
    [Theory]
    [InlineData("clip.MP4", "Video")]
    [InlineData("song.mp3", "Music")]
    [InlineData("setup.exe", "Programs")]
    [InlineData("paper.pdf", "Documents")]
    [InlineData("backup.zip", "Compressed")]
    [InlineData("mystery.xyz", "Other")]
    [InlineData("noextension", "Other")]
    public void Categories_ByExtension(string file, string expected) => Assert.Equal(expected, Categories.For(file));

    [Fact]
    public void Categories_FolderIsUnderRootAndFileSafe()
    {
        var s = new AppSettings { DownloadRoot = @"C:\Dl" };
        Assert.Equal(@"C:\Dl\Video", Categories.FolderFor(s, "Video"));
        Assert.Equal(@"C:\Dl\Other", Categories.FolderFor(s, "All Downloads"));
    }

    [Fact]
    public void Categories_FileNameFromUrl()
    {
        Assert.Equal("a b.mp4", Categories.NameFromUrl(new Uri("http://x/dir/a%20b.mp4?token=1")));
        Assert.Equal("download", Categories.NameFromUrl(new Uri("http://x/")));
    }

    // ---- settings ----
    [Fact]
    public void Settings_RoundTrip_AndCorruptFileFallsBackToDefaults()
    {
        var path = Path.Combine(Tmp(), "settings.json");
        new AppSettings { DownloadRoot = @"D:\Stuff", AlwaysAskQuality = true, ThemeMode = "Light" }.Save(path);
        var loaded = AppSettings.Load(path);
        Assert.Equal(@"D:\Stuff", loaded.DownloadRoot);
        Assert.True(loaded.AlwaysAskQuality);
        Assert.Equal("Light", loaded.ThemeMode);
        File.WriteAllText(path, "{ nope");
        Assert.Equal("System", AppSettings.Load(path).ThemeMode);
        Assert.Equal("System", AppSettings.Load(path + ".missing").ThemeMode);
    }

    // ---- clipboard ----
    [Fact]
    public void Clipboard_UrlOfConfiguredType_RaisesPrompt()
    {
        var f = new ClipboardUrlFilter(() => new AppSettings());
        Assert.True(f.TryGet("  https://x.example/files/setup.exe?id=3 \r\n", out var url));
        Assert.Equal("https://x.example/files/setup.exe?id=3", url!.ToString());
        Assert.False(f.TryGet("https://x.example/files/setup.exe?id=3", out _), "same text twice in a row is ignored");
        Assert.False(f.TryGet("https://x.example/index.html", out _));
        Assert.False(f.TryGet("just some words .zip", out _));
        Assert.False(f.TryGet(null, out _));
        Assert.False(f.TryGet("javascript:alert(1)//a.zip", out _));
        Assert.True(f.TryGet("ftp://files.example/pub/data.zip", out _));
        Assert.True(f.TryGet("https://x.example/files/setup.exe?id=3", out _), "after something else, it prompts again");
    }

    [Fact]
    public void Clipboard_RespectsConfiguredTypes()
    {
        var s = new AppSettings { ClipboardTypes = new() { "iso" } };
        var f = new ClipboardUrlFilter(() => s);
        Assert.False(f.TryGet("https://x/a.zip", out _));
        Assert.True(f.TryGet("https://x/a.iso", out _));
    }

    // ---- pipe server + handler ----
    static (DownloadManager M, DownloadStore S) NewManager(int maxConcurrent = 0)
    {
        var store = new DownloadStore(Path.Combine(Tmp(), "bdm.db"));
        store.SaveQueue(new QueueDefinition("main", "Main", maxConcurrent, 4, null, null, PostAction.None));
        var m = new DownloadManager(store, _ => new HttpClient());
        m.Streams = new StreamDownloader(new HttpClient(), new FfmpegLocator(null, _ => null));
        return (m, store);
    }

    sealed class FakePicker(int? pick) : IQualityPicker
    {
        public int Asked;
        public Task<StreamChoice?> PickAsync(IReadOnlyList<StreamChoice> choices, string title)
        {
            Asked++;
            return Task.FromResult(pick is { } i ? choices[i] : null);
        }
    }

    const string Master = "#EXTM3U\n" +
        "#EXT-X-STREAM-INF:BANDWIDTH=500000,RESOLUTION=320x240\nlow/index.m3u8\n" +
        "#EXT-X-STREAM-INF:BANDWIDTH=900000,RESOLUTION=640x480\nhigh/index.m3u8\n";

    static HostMessage Add(string url) => new("add", url, "https://page/", "T", new() { ["Referer"] = "https://page/" }, "a=b", null, null);

    [Fact]
    public async Task PipeAdd_CreatesItem_AndRepliesOk()
    {
        var (m, _) = NewManager();
        var settings = new AppSettings { DownloadRoot = Tmp() };
        var name = "bdm-test-" + Guid.NewGuid().ToString("N");
        using var server = new PipeServer(name, new HostMessageHandler(m, settings, new FakePicker(null)).HandleAsync);
        server.Start();

        var reply = await PipeClient.SendAsync(name, Add("https://files.example/a/video.mp4"), TimeSpan.FromSeconds(5));

        Assert.True(reply.Ok);
        var rec = Assert.Single(m.All());
        Assert.Equal("https://files.example/a/video.mp4", rec.Request.Url.ToString());
        Assert.Equal("a=b", rec.Request.CookieHeader);
        Assert.Equal("https://page/", rec.Request.Referer);
        Assert.Equal(Path.Combine(settings.DownloadRoot, "Video"), rec.Request.DestinationDir);
        Assert.Equal("main", rec.QueueId);
    }

    [Fact]
    public async Task PipeServer_ToleratesEmptyProbeConnections_ThenServesRealOnes()
    {
        var (m, _) = NewManager();
        var name = "bdm-test-" + Guid.NewGuid().ToString("N");
        using var server = new PipeServer(name, new HostMessageHandler(m, new AppSettings { DownloadRoot = Tmp() }, new FakePicker(null)).HandleAsync);
        server.Start();
        for (var i = 0; i < 3; i++)
        {
            await using var probe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            await probe.ConnectAsync(2000); // connect and leave, like the host's liveness probe
        }
        Assert.True((await PipeClient.SendAsync(name, new HostMessage("ping", null, null, null, null, null, null, null), TimeSpan.FromSeconds(5))).Ok);
    }

    [Fact]
    public async Task Handler_Garbage_RepliesError()
    {
        var (m, _) = NewManager();
        var h = new HostMessageHandler(m, new AppSettings { DownloadRoot = Tmp() }, new FakePicker(null));
        Assert.False((await h.HandleAsync(new HostMessage("add", "not a url", null, null, null, null, null, null))).Ok);
        Assert.False((await h.HandleAsync(new HostMessage("add", "file:///c:/windows/x.exe", null, null, null, null, null, null))).Ok);
        Assert.False((await h.HandleAsync(new HostMessage("explode", null, null, null, null, null, null, null))).Ok);
        Assert.Empty(m.All());
    }

    static HostMessage Stream(string op) => new(op, "https://cdn.example/master.m3u8", "https://page/", "My clip", new(), null, Master, "hls");

    [Fact]
    public async Task Handler_AddStream_UsesBestQualityByDefault_AndAsksWhenAskedFor()
    {
        var (m, _) = NewManager();
        var picker = new FakePicker(1);
        var h = new HostMessageHandler(m, new AppSettings { DownloadRoot = Tmp() }, picker);

        Assert.True((await h.HandleAsync(Stream("addStream"))).Ok);
        Assert.Equal(0, picker.Asked);
        Assert.Contains("640x480", m.All()[0].StreamChoice!.Label);

        Assert.True((await h.HandleAsync(Stream("addStreamAsk"))).Ok);
        Assert.Equal(1, picker.Asked);
        Assert.Contains("320x240", m.All()[1].StreamChoice!.Label); // the picker chose the second (lower) entry
        Assert.Equal(Path.Combine(h.Settings.DownloadRoot, "Video"), m.All()[1].Request.DestinationDir);
    }

    [Fact]
    public async Task Handler_AlwaysAskQualitySetting_AsksOnPlainAddStream()
    {
        var (m, _) = NewManager();
        var picker = new FakePicker(0);
        var h = new HostMessageHandler(m, new AppSettings { DownloadRoot = Tmp(), AlwaysAskQuality = true }, picker);
        Assert.True((await h.HandleAsync(Stream("addStream"))).Ok);
        Assert.Equal(1, picker.Asked);
    }

    [Fact]
    public async Task Handler_PickerCancelled_RepliesNotOkAndAddsNothing()
    {
        var (m, _) = NewManager();
        var h = new HostMessageHandler(m, new AppSettings { DownloadRoot = Tmp() }, new FakePicker(null));
        var r = await h.HandleAsync(Stream("addStreamAsk"));
        Assert.False(r.Ok);
        Assert.Contains("cancel", r.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(m.All());
    }

    [Fact]
    public async Task Handler_ProtectedDash_RepliesProtectedWithoutAdding()
    {
        var (m, _) = NewManager();
        var dash = "<MPD xmlns=\"urn:mpeg:dash:schema:mpd:2011\" type=\"static\" mediaPresentationDuration=\"PT4S\"><Period><AdaptationSet mimeType=\"video/mp4\">" +
            "<ContentProtection schemeIdUri=\"urn:uuid:edef8ba9-79d6-4ace-a3c8-27dcd51d21ed\"/><SegmentTemplate timescale=\"1\" duration=\"4\" media=\"s$Number$.m4s\"/>" +
            "<Representation id=\"v\" bandwidth=\"1\"/></AdaptationSet></Period></MPD>";
        var h = new HostMessageHandler(m, new AppSettings { DownloadRoot = Tmp() }, new FakePicker(0));
        var r = await h.HandleAsync(new HostMessage("addStream", "https://cdn.example/m.mpd", null, "t", new(), null, dash, "dash"));
        Assert.False(r.Ok);
        Assert.Contains("protected", r.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(m.All());
    }

    [Fact]
    public async Task Handler_Ping_IsOk()
    {
        var (m, _) = NewManager();
        var h = new HostMessageHandler(m, new AppSettings(), new FakePicker(null));
        Assert.True((await h.HandleAsync(new HostMessage("ping", null, null, null, null, null, null, null))).Ok);
    }

    // ---- single instance ----
    [Fact]
    public async Task SecondInstance_ForwardsToFirst()
    {
        var id = Guid.NewGuid().ToString("N");
        var pipe = "bdm-test-" + id;
        var received = new List<HostMessage>();
        using var first = new SingleInstance(id);
        Assert.True(first.IsFirst);
        using var server = new PipeServer(pipe, msg => { lock (received) received.Add(msg); return Task.FromResult(new HostReply(true, null, false)); });
        server.Start();

        using var second = new SingleInstance(id);
        Assert.False(second.IsFirst);
        await second.ForwardAsync(pipe, new[] { "https://files.example/a.zip" });
        await second.ForwardAsync(pipe, Array.Empty<string>());

        lock (received)
        {
            Assert.Equal(2, received.Count);
            Assert.Equal("add", received[0].Op);
            Assert.Equal("https://files.example/a.zip", received[0].Url);
            Assert.Equal("activate", received[1].Op);
        }
    }
}
