using BDM.Core.Engine;
using BDM.Core.Streams;

namespace BDM.Core.Tests;

public class StreamDownloaderTests : IClassFixture<StreamMedia>
{
    readonly StreamMedia _m;
    static readonly HttpClient Http = new();
    static readonly TimeSpan Fast = TimeSpan.FromMilliseconds(10);

    public StreamDownloaderTests(StreamMedia m) => _m = m;

    StreamDownloader Make(FfmpegLocator? ff = null) => new(Http, ff ?? new FfmpegLocator(_m.Ffmpeg));
    static string NewDir() => Directory.CreateTempSubdirectory().FullName;
    static StreamOptions Opts(bool mergeSubs = false) => new(MergeSubtitles: mergeSubs, LivePollInterval: TimeSpan.FromMilliseconds(120), RetryBaseDelay: Fast);

    static StreamSource Hls(Uri url, Dictionary<string, string>? headers = null, string? cookie = null, string? body = null, Uri? thumb = null) =>
        new(url, StreamKind.Hls, headers ?? new(), cookie, "My Video", body, thumb);

    [Fact]
    public async Task Hls_Clear_ProducesPlayableMp4()
    {
        await using var server = new StreamServer(_m.Root);
        var dl = Make();
        var src = Hls(server.Url("hls/index.m3u8"));
        var choice = Assert.Single(await dl.ListChoicesAsync(src, default));
        DownloadProgress? last = null;
        var path = await dl.DownloadAsync(src, choice, NewDir(), new Progress1(p => last = p), default, Opts());
        var probe = _m.ProbeFile(path);
        Assert.EndsWith(".mp4", path);
        Assert.Equal(6, probe.Duration, 0.6);
        Assert.True(probe.HasVideo && probe.HasAudio);
        Assert.True(last!.Downloaded > 0);
    }

    sealed class Progress1(Action<DownloadProgress> on) : IProgress<DownloadProgress> { public void Report(DownloadProgress v) => on(v); }

    [Fact]
    public async Task Hls_Master_ListsChoicesBestFirst_WithSubtitleTracks()
    {
        await using var server = new StreamServer(_m.Root);
        var choices = await Make().ListChoicesAsync(Hls(server.Url("master.m3u8")), default);
        Assert.Equal(2, choices.Count);
        Assert.Contains("640x480", choices[0].Label);
        Assert.Contains("320x240", choices[1].Label);
        Assert.Single(choices[0].Subtitles);
        Assert.Null(choices[0].Audio);
    }

    [Fact]
    public async Task Hls_Aes128_DecryptsAndMerges()
    {
        await using var server = new StreamServer(_m.Root);
        var dl = Make();
        var src = Hls(server.Url("hlsaes/index.m3u8"));
        var path = await dl.DownloadAsync(src, (await dl.ListChoicesAsync(src, default))[0], NewDir(), null, default, Opts());
        var probe = _m.ProbeFile(path);
        Assert.Equal(6, probe.Duration, 0.6);
        Assert.True(probe.HasVideo);
        Assert.Contains(server.Requests, r => r.Path == "/hlsaes/key.bin");
    }

    [Fact]
    public async Task Hls_Fmp4_WithMap_Merges()
    {
        await using var server = new StreamServer(_m.Root);
        var dl = Make();
        var src = Hls(server.Url("hlsfmp4/index.m3u8"));
        var path = await dl.DownloadAsync(src, (await dl.ListChoicesAsync(src, default))[0], NewDir(), null, default, Opts());
        Assert.Equal(6, _m.ProbeFile(path).Duration, 0.6);
        Assert.Contains(server.Requests, r => r.Path == "/hlsfmp4/init.mp4");
    }

    [Fact]
    public async Task Dash_SeparateVideoAudio_PairedAndMuxed()
    {
        await using var server = new StreamServer(_m.Root);
        var dl = Make();
        var src = new StreamSource(server.Url("dash/manifest.mpd"), StreamKind.Dash, new Dictionary<string, string>(), null, "Dash Video", null);
        var choice = (await dl.ListChoicesAsync(src, default))[0];
        Assert.NotNull(choice.Audio);
        var path = await dl.DownloadAsync(src, choice, NewDir(), null, default, Opts());
        var probe = _m.ProbeFile(path);
        Assert.True(probe.HasVideo && probe.HasAudio);
        Assert.Equal(6, probe.Duration, 0.7);
    }

    [Fact]
    public async Task Live_NoEndlist_StopsOnCancelAndKeepsPlayableFile()
    {
        await using var server = new StreamServer(_m.Root);
        server.Live = poll => LivePlaylist(poll, ended: false);
        var dl = Make();
        var src = Hls(server.Url("live.m3u8"));
        var choice = (await dl.ListChoicesAsync(src, default))[0];
        using var cts = new CancellationTokenSource();
        var progress = new Progress1(_ => { if (server.LivePolls >= 4) cts.Cancel(); });
        var path = await dl.DownloadAsync(src, choice, NewDir(), progress, cts.Token, Opts());
        var probe = _m.ProbeFile(path);
        Assert.True(probe.Duration > 3, $"duration {probe.Duration}");
        Assert.True(probe.HasVideo);
    }

    [Fact]
    public async Task Live_EndlistAppears_StopsAutomatically()
    {
        await using var server = new StreamServer(_m.Root);
        server.Live = poll => LivePlaylist(poll, ended: poll >= 3);
        var dl = Make();
        var src = Hls(server.Url("live.m3u8"));
        var choice = (await dl.ListChoicesAsync(src, default))[0];
        var path = await dl.DownloadAsync(src, choice, NewDir(), null, default, Opts());
        Assert.Equal(6, _m.ProbeFile(path).Duration, 0.7);
    }

    /// <summary>Grows from 1 to 3 segments (poll 0 = the listing request), optionally ending.</summary>
    static string LivePlaylist(int poll, bool ended)
    {
        var count = Math.Clamp(poll, 1, 3);
        var text = "#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:2\n#EXT-X-MEDIA-SEQUENCE:0\n";
        for (var i = 0; i < count; i++) text += $"#EXTINF:2.0,\nhls/seg{i}.ts\n";
        return ended ? text + "#EXT-X-ENDLIST\n" : text;
    }

    [Fact]
    public async Task Protected_Throws_ProtectedContentException()
    {
        var dash = """
            <MPD xmlns="urn:mpeg:dash:schema:mpd:2011" type="static" mediaPresentationDuration="PT4S"><Period>
              <AdaptationSet mimeType="video/mp4"><ContentProtection schemeIdUri="urn:uuid:edef8ba9-79d6-4ace-a3c8-27dcd51d21ed"/>
                <SegmentTemplate timescale="1" duration="4" media="s$Number$.m4s"/><Representation id="v" bandwidth="1"/></AdaptationSet>
            </Period></MPD>
            """;
        var dl = Make();
        var dashSrc = new StreamSource(new Uri("https://x.example/m.mpd"), StreamKind.Dash, new Dictionary<string, string>(), null, "t", dash);
        await Assert.ThrowsAsync<ProtectedContentException>(() => dl.ListChoicesAsync(dashSrc, default));

        var hls = "#EXTM3U\n#EXT-X-TARGETDURATION:4\n#EXT-X-KEY:METHOD=SAMPLE-AES,URI=\"skd://k\"\n#EXTINF:4,\na.ts\n#EXT-X-ENDLIST\n";
        var hlsSrc = Hls(new Uri("https://x.example/m.m3u8"), body: hls);
        var choice = (await dl.ListChoicesAsync(hlsSrc, default))[0];
        await Assert.ThrowsAsync<ProtectedContentException>(() => dl.DownloadAsync(hlsSrc, choice, NewDir(), null, default, Opts()));
    }

    [Fact]
    public async Task Subtitles_WebVtt_SavedNextToVideo_And_MergedWhenRequested()
    {
        await using var server = new StreamServer(_m.Root);
        var dl = Make();
        var src = Hls(server.Url("master.m3u8"));
        var choice = (await dl.ListChoicesAsync(src, default))[0];

        var dir1 = NewDir();
        var plain = await dl.DownloadAsync(src, choice, dir1, null, default, Opts());
        var vtt = Assert.Single(Directory.GetFiles(dir1, "*.vtt"));
        var text = File.ReadAllText(vtt);
        Assert.StartsWith("WEBVTT", text);
        Assert.Equal(1, text.Split("WEBVTT").Length - 1); // segment headers are not repeated
        Assert.Contains("Hello", text);
        Assert.Contains("World", text);
        Assert.False(_m.ProbeFile(plain).HasSubtitles);

        var merged = await dl.DownloadAsync(src, choice, NewDir(), null, default, Opts(mergeSubs: true));
        Assert.True(_m.ProbeFile(merged).HasSubtitles);
    }

    [Fact]
    public async Task Thumbnail_SavedWhenPresent()
    {
        await using var server = new StreamServer(_m.Root);
        var dl = Make();
        var src = Hls(server.Url("hls/index.m3u8"), thumb: server.Url("thumb.jpg"));
        var dir = NewDir();
        var path = await dl.DownloadAsync(src, (await dl.ListChoicesAsync(src, default))[0], dir, null, default, Opts());
        var jpg = Assert.Single(Directory.GetFiles(dir, "*.jpg"));
        Assert.Equal(Path.GetFileNameWithoutExtension(path), Path.GetFileNameWithoutExtension(jpg));
        Assert.True(new FileInfo(jpg).Length > 100);
    }

    [Fact]
    public async Task Ffmpeg_Missing_ThrowsActionableError()
    {
        await using var server = new StreamServer(_m.Root);
        var dl = Make(new FfmpegLocator(null, _ => null));
        var src = Hls(server.Url("hls/index.m3u8"));
        var choice = (await Make().ListChoicesAsync(src, default))[0];
        var ex = await Assert.ThrowsAsync<FfmpegMissingException>(() => dl.DownloadAsync(src, choice, NewDir(), null, default, Opts()));
        Assert.Contains("ffmpeg", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Options", ex.Message);
        Assert.DoesNotContain(server.Requests, r => r.Path.EndsWith(".ts")); // failed fast, before fetching segments
    }

    [Fact]
    public async Task Headers_AndCookie_SentOnEverySegmentRequest()
    {
        await using var server = new StreamServer(_m.Root);
        var dl = Make();
        var headers = new Dictionary<string, string> { ["Referer"] = "https://page.example/watch" };
        var src = Hls(server.Url("hlsaes/index.m3u8"), headers, "sid=1");
        var choice = (await dl.ListChoicesAsync(src, default))[0];
        await dl.DownloadAsync(src, choice, NewDir(), null, default, Opts());
        Assert.True(server.Requests.Count >= 5, "playlist (x2), key and three segments expected");
        Assert.All(server.Requests, r => { Assert.Equal("https://page.example/watch", r.Referer); Assert.Equal("sid=1", r.Cookie); });
    }

    [Fact]
    public async Task Live_PlaylistGoesAway_AfterSomeSegments_FinalizesWhatWasCaptured()
    {
        await using var server = new StreamServer(_m.Root);
        server.Live = poll => LivePlaylist(poll, ended: false);
        server.LiveStatus = poll => poll >= 4 ? 403 : 200; // the tokenized playlist link expires an hour into a capture
        var dl = Make();
        var src = Hls(server.Url("live.m3u8"));
        var choice = (await dl.ListChoicesAsync(src, default))[0];
        var path = await dl.DownloadAsync(src, choice, NewDir(), null, default, Opts());
        var probe = _m.ProbeFile(path);
        Assert.True(probe.Duration > 3, $"duration {probe.Duration}");
        Assert.True(probe.HasVideo);
    }

    [Fact]
    public void MergeArguments_MapOnlyVideoAndAudio_NeverEverythingInTheFile()
    {
        // "-map 0" drags data/subtitle streams (timed ID3, DVB subtitles...) into the mp4 muxer, which cannot hold them.
        var single = StreamDownloader.BuildMergeArgs(["in.ts"], separateAudio: false, [], "out.mp4");
        Assert.Contains("0:v?", single);
        Assert.Contains("0:a?", single);
        Assert.DoesNotContain("0", single.Where((a, i) => i > 0 && single[i - 1] == "-map" && a == "0")); // never a bare "-map 0"
        Assert.Equal("out.mp4", single[^1]);

        var split = StreamDownloader.BuildMergeArgs(["v.mp4", "a.mp4"], separateAudio: true, [], "out.mp4");
        Assert.Equal(new[] { "-map", "0:v", "-map", "1:a" }, split.SkipWhile(a => a != "-map").Take(4));

        var withSubs = StreamDownloader.BuildMergeArgs(["v.ts"], false, ["s.vtt"], "out.mp4");
        Assert.Contains("1", withSubs.Where((a, i) => i > 0 && withSubs[i - 1] == "-map"));
        Assert.Contains("mov_text", withSubs);
        Assert.Equal(new[] { "-i", "v.ts", "-i", "s.vtt" }, withSubs.Take(4));
    }
}
