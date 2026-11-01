using BDM.Core.Engine;
using BDM.Core.Ext;

namespace BDM.Core.Tests;

public class YtDlpRunnerTests
{
    const string InfoJson = """
        {"title":"My Video","id":"abc123","is_live":false,"formats":[
          {"format_id":"sb0","ext":"mhtml","vcodec":"none","acodec":"none","protocol":"mhtml"},
          {"format_id":"140","ext":"m4a","vcodec":"none","acodec":"mp4a.40.2","abr":129,"tbr":129},
          {"format_id":"134","ext":"mp4","vcodec":"avc1.4d401e","acodec":"none","height":360,"width":640,"tbr":400},
          {"format_id":"137","ext":"mp4","vcodec":"avc1.640028","acodec":"none","height":1080,"width":1920,"tbr":2500},
          {"format_id":"136","ext":"mp4","vcodec":"avc1.4d401f","acodec":"none","height":720,"width":1280,"tbr":1100},
          {"format_id":"136b","ext":"webm","vcodec":"vp9","acodec":"none","height":720,"width":1280,"tbr":900},
          {"format_id":"18","ext":"mp4","vcodec":"avc1","acodec":"mp4a","height":360,"tbr":500}
        ]}
        """;

    sealed class FakeProcess(int exit = 0, string stdout = "", string stderr = "", string[]? lines = null) : IYtDlpProcess
    {
        public List<IReadOnlyList<string>> Calls { get; } = new();
        public Action<IReadOnlyList<string>>? OnRun { get; set; }
        public Task<(int Exit, string StdOut, string StdErr)> RunAsync(IReadOnlyList<string> args, Action<string>? onLine, CancellationToken ct)
        {
            Calls.Add(args);
            OnRun?.Invoke(args);
            foreach (var l in lines ?? []) onLine?.Invoke(l);
            return Task.FromResult((exit, stdout, stderr));
        }
    }

    static YtDlpRunner Make(FakeProcess p, string? exe = "C:\\tools\\yt-dlp.exe", string? ffmpeg = "C:\\ff\\ffmpeg.exe") =>
        new(() => exe, _ => p, () => ffmpeg);

    [Fact]
    public async Task Info_ParsesFormats_BestFirst_AudioOnlyLast_StoryboardIgnored()
    {
        var info = await Make(new FakeProcess(stdout: InfoJson)).InfoAsync(new Uri("https://v.example/watch?v=1"), null, default);
        Assert.Equal("My Video", info.Title);
        Assert.False(info.IsLive);
        Assert.Equal(new[] { "1080p", "720p", "360p", "Audio only" }, info.Choices.Select(c => c.Label));
        Assert.Equal("bv*[height<=1080]+ba/b[height<=1080]", info.Choices[0].Video.Id);
        Assert.Equal("ba/b", info.Choices[^1].Video.Id);
        Assert.All(info.Choices, c => Assert.Equal(new Uri("https://v.example/watch?v=1"), c.Video.Playlist));
    }

    [Fact]
    public async Task Info_PassesHeadersAndNoPlaylist_ToYtDlp()
    {
        var p = new FakeProcess(stdout: InfoJson);
        await Make(p).InfoAsync(new Uri("https://v.example/w"), new Dictionary<string, string> { ["Cookie"] = "a=b", ["Referer"] = "https://v.example/" }, default);
        var args = p.Calls.Single();
        Assert.Contains("-J", args);
        Assert.Contains("--no-playlist", args);
        Assert.Contains("Cookie: a=b", args);
        Assert.Contains("Referer: https://v.example/", args);
        Assert.Equal("https://v.example/w", args[^1]);
    }

    [Fact]
    public async Task Info_LiveStream_IsFlagged()
    {
        var info = await Make(new FakeProcess(stdout: InfoJson.Replace("\"is_live\":false", "\"is_live\":true"))).InfoAsync(new Uri("https://v/x"), null, default);
        Assert.True(info.IsLive);
    }

    [Fact]
    public async Task Info_MissingExe_ThrowsActionableError()
    {
        var ex = await Assert.ThrowsAsync<YtDlpMissingException>(() => Make(new FakeProcess(), exe: null).InfoAsync(new Uri("https://v/x"), null, default));
        Assert.Contains("yt-dlp", ex.Message);
        Assert.Contains("Options", ex.Message);
    }

    [Fact]
    public async Task Info_YtDlpFailure_SurfacesItsErrorLine()
    {
        var p = new FakeProcess(exit: 1, stderr: "WARNING: something\nERROR: [generic] Unsupported URL: https://v/x\n");
        var ex = await Assert.ThrowsAsync<YtDlpException>(() => Make(p).InfoAsync(new Uri("https://v/x"), null, default));
        Assert.Contains("Unsupported URL", ex.Message);
    }

    [Fact]
    public async Task Info_GarbageOutput_ThrowsYtDlpException()
    {
        await Assert.ThrowsAsync<YtDlpException>(() => Make(new FakeProcess(stdout: "not json")).InfoAsync(new Uri("https://v/x"), null, default));
        await Assert.ThrowsAsync<YtDlpException>(() => Make(new FakeProcess(stdout: "{\"title\":\"x\"}")).InfoAsync(new Uri("https://v/x"), null, default));
    }

    [Fact]
    public async Task Download_BuildsArguments_ParsesProgressAndReturnsTheFinalPath()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var final = Path.Combine(dir, "My Video.mp4");
        var p = new FakeProcess(lines: ["[info] something", "BDMPROGRESS 250 1000 NA", "BDMPROGRESS 800 NA 1200", $"BDMFILE {final}"]);
        var seen = new List<DownloadProgress>();

        var path = await Make(p).DownloadAsync(new Uri("https://v.example/w"), "bv*[height<=720]+ba/b[height<=720]", dir,
            new Dictionary<string, string> { ["Cookie"] = "a=b" }, new Progress1(seen.Add), default);

        Assert.Equal(final, path);
        var args = p.Calls.Single();
        Assert.Equal("bv*[height<=720]+ba/b[height<=720]", args[args.ToList().IndexOf("-f") + 1]);
        Assert.Contains("--no-playlist", args);
        Assert.Contains("--newline", args);
        Assert.Equal("mp4", args[args.ToList().IndexOf("--merge-output-format") + 1]);
        Assert.Equal(@"C:\ff\ffmpeg.exe", args[args.ToList().IndexOf("--ffmpeg-location") + 1]);
        Assert.StartsWith(dir, args[args.ToList().IndexOf("-o") + 1]);
        Assert.Contains("Cookie: a=b", args);
        Assert.Equal("https://v.example/w", args[^1]);
        Assert.Equal((250L, 1000L), (seen[0].Downloaded, seen[0].Total!.Value));
        Assert.Equal((800L, 1200L), (seen[1].Downloaded, seen[1].Total!.Value)); // estimate used when the exact total is NA
    }

    sealed class Progress1(Action<DownloadProgress> on) : IProgress<DownloadProgress> { public void Report(DownloadProgress v) => on(v); }

    [Fact]
    public async Task Download_NonZeroExit_ThrowsWithTheErrorLine()
    {
        var p = new FakeProcess(exit: 1, stderr: "ERROR: Sign in to confirm you're not a bot\n");
        var ex = await Assert.ThrowsAsync<YtDlpException>(() => Make(p).DownloadAsync(new Uri("https://v/x"), "b", Path.GetTempPath(), null, null, default));
        Assert.Contains("Sign in", ex.Message);
    }

    [Fact]
    public async Task Download_NoFileReported_IsAnError()
    {
        await Assert.ThrowsAsync<YtDlpException>(() => Make(new FakeProcess(lines: ["BDMPROGRESS 1 2 NA"])).DownloadAsync(new Uri("https://v/x"), "b", Path.GetTempPath(), null, null, default));
    }

    [Fact]
    public async Task Download_WithoutKnownFfmpeg_OmitsTheLocationFlag()
    {
        var p = new FakeProcess(lines: ["BDMFILE C:\\x.mp4"]);
        await Make(p, ffmpeg: null).DownloadAsync(new Uri("https://v/x"), "b", Path.GetTempPath(), null, null, default);
        Assert.DoesNotContain("--ffmpeg-location", p.Calls.Single());
    }

    [Fact]
    public async Task ProxyUrl_IsPassedToBothInfoAndDownload()
    {
        var info = new FakeProcess(stdout: InfoJson);
        await Make(info).InfoAsync(new Uri("https://v/x"), null, default, "socks5://u:p@h:1080");
        Assert.Equal("socks5://u:p@h:1080", info.Calls.Single()[info.Calls.Single().ToList().IndexOf("--proxy") + 1]);

        var dl = new FakeProcess(lines: ["BDMFILE C:\\x.mp4"]);
        await Make(dl).DownloadAsync(new Uri("https://v/x"), "b", Path.GetTempPath(), null, null, default, "http://h:8080");
        Assert.Equal("http://h:8080", dl.Calls.Single()[dl.Calls.Single().ToList().IndexOf("--proxy") + 1]);

        var none = new FakeProcess(stdout: InfoJson);
        await Make(none).InfoAsync(new Uri("https://v/x"), null, default);
        Assert.DoesNotContain("--proxy", none.Calls.Single());
    }
}
