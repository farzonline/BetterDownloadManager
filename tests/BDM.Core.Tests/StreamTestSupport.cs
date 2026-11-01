using System.Diagnostics;
using System.Text.Json;
using BDM.Core.Streams;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace BDM.Core.Tests;

/// <summary>Real HLS/DASH media generated once with ffmpeg and served from a local server.</summary>
public sealed class StreamMedia : IDisposable
{
    public string Root { get; } = Directory.CreateTempSubdirectory("bdm-streams").FullName;
    public string Ffmpeg { get; } = FfmpegLocator.Find(null) ?? throw new InvalidOperationException("ffmpeg is required for the stream tests");
    public string Ffprobe { get; }

    public StreamMedia()
    {
        Ffprobe = FfmpegLocator.FindProbe(Ffmpeg) ?? throw new InvalidOperationException("ffprobe is required for the stream tests");
        var src = new[]
        {
            "-f", "lavfi", "-i", "testsrc=duration=6:size=320x240:rate=15",
            "-f", "lavfi", "-i", "sine=frequency=440:duration=6",
            "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-g", "30", "-c:a", "aac", "-shortest",
        };
        Run(src.Concat(new[] { "-f", "hls", "-hls_time", "2", "-hls_playlist_type", "vod", "-hls_segment_filename", Sub("hls", "seg%d.ts"), Sub("hls", "index.m3u8") }));

        var key = new byte[16];
        new Random(3).NextBytes(key);
        Directory.CreateDirectory(Path.Combine(Root, "hlsaes"));
        File.WriteAllBytes(Path.Combine(Root, "hlsaes", "key.bin"), key);
        File.WriteAllText(Path.Combine(Root, "keyinfo.txt"), $"key.bin\n{Path.Combine(Root, "hlsaes", "key.bin")}\n00112233445566778899aabbccddeeff\n");
        Run(src.Concat(new[] { "-f", "hls", "-hls_time", "2", "-hls_playlist_type", "vod", "-hls_key_info_file", Path.Combine(Root, "keyinfo.txt"),
            "-hls_segment_filename", Sub("hlsaes", "seg%d.ts"), Sub("hlsaes", "index.m3u8") }));

        Run(src.Concat(new[] { "-f", "hls", "-hls_time", "2", "-hls_playlist_type", "vod", "-hls_segment_type", "fmp4", "-hls_fmp4_init_filename", "init.mp4",
            "-hls_segment_filename", Sub("hlsfmp4", "seg%d.m4s"), Sub("hlsfmp4", "index.m3u8") }), cwd: Path.Combine(Root, "hlsfmp4"));

        Run(src.Concat(new[] { "-f", "dash", "-seg_duration", "2", "-use_template", "1", "-use_timeline", "1", Sub("dash", "manifest.mpd") }), cwd: Path.Combine(Root, "dash"));

        Run(new[] { "-f", "lavfi", "-i", "testsrc=duration=1:size=160x120:rate=1", "-frames:v", "1", Path.Combine(Root, "thumb.jpg") });

        Directory.CreateDirectory(Path.Combine(Root, "subs"));
        File.WriteAllText(Path.Combine(Root, "subs", "en0.vtt"), "WEBVTT\n\n00:00:00.500 --> 00:00:02.000\nHello\n");
        File.WriteAllText(Path.Combine(Root, "subs", "en1.vtt"), "WEBVTT\nX-TIMESTAMP-MAP=MPEGTS:900000,LOCAL:00:00:00.000\n\n00:00:02.500 --> 00:00:04.000\nWorld\n");
        File.WriteAllText(Path.Combine(Root, "subs", "en.m3u8"),
            "#EXTM3U\n#EXT-X-TARGETDURATION:3\n#EXTINF:3,\nen0.vtt\n#EXTINF:3,\nen1.vtt\n#EXT-X-ENDLIST\n");

        File.WriteAllText(Path.Combine(Root, "master.m3u8"),
            "#EXTM3U\n" +
            "#EXT-X-MEDIA:TYPE=SUBTITLES,GROUP-ID=\"subs\",NAME=\"English\",LANGUAGE=\"en\",URI=\"subs/en.m3u8\"\n" +
            "#EXT-X-STREAM-INF:BANDWIDTH=500000,RESOLUTION=320x240,SUBTITLES=\"subs\"\nhls/index.m3u8\n" +
            "#EXT-X-STREAM-INF:BANDWIDTH=900000,RESOLUTION=640x480,SUBTITLES=\"subs\"\nhls/index.m3u8?hi=1\n");
        File.WriteAllText(Path.Combine(Root, "master-nosubs.m3u8"),
            "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=500000,RESOLUTION=320x240\nhls/index.m3u8\n");
    }

    string Sub(string dir, string file)
    {
        Directory.CreateDirectory(Path.Combine(Root, dir));
        return Path.Combine(Root, dir, file);
    }

    void Run(IEnumerable<string> args, string? cwd = null)
    {
        var psi = new ProcessStartInfo(Ffmpeg) { RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = cwd ?? Root };
        foreach (var a in new[] { "-y", "-hide_banner", "-loglevel", "error" }.Concat(args)) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException("ffmpeg fixture failed: " + err);
    }

    public Probe ProbeFile(string path)
    {
        var psi = new ProcessStartInfo(Ffprobe) { RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var a in new[] { "-v", "error", "-print_format", "json", "-show_format", "-show_streams", path }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var json = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        using var doc = JsonDocument.Parse(json);
        var streams = doc.RootElement.GetProperty("streams").EnumerateArray().Select(s => s.GetProperty("codec_type").GetString()!).ToList();
        var dur = double.Parse(doc.RootElement.GetProperty("format").GetProperty("duration").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
        return new Probe(dur, streams);
    }

    public record Probe(double Duration, List<string> Streams)
    {
        public bool HasVideo => Streams.Contains("video");
        public bool HasAudio => Streams.Contains("audio");
        public bool HasSubtitles => Streams.Contains("subtitle");
    }

    public void Dispose() { try { Directory.Delete(Root, true); } catch (IOException) { } }
}

public sealed class StreamServer : IAsyncDisposable
{
    readonly WebApplication _app;
    int _livePolls;
    public List<(string Path, string? Referer, string? Cookie)> Requests { get; } = new();
    public Func<int, string>? Live { get; set; }
    /// <summary>HTTP status for the live playlist per poll (default 200).</summary>
    public Func<int, int>? LiveStatus { get; set; }
    public int LivePolls => Volatile.Read(ref _livePolls);
    public Uri Base { get; }

    public StreamServer(string root)
    {
        var b = WebApplication.CreateBuilder();
        b.Logging.ClearProviders();
        b.WebHost.UseUrls("http://127.0.0.1:0");
        _app = b.Build();
        _app.MapFallback("{*path}", async ctx =>
        {
            var path = ctx.Request.Path.Value!;
            lock (Requests) Requests.Add((path, ctx.Request.Headers.Referer.ToString(), ctx.Request.Headers.Cookie.ToString()));
            if (path == "/live.m3u8" && Live != null)
            {
                var poll = Interlocked.Increment(ref _livePolls) - 1;
                var status = LiveStatus?.Invoke(poll) ?? 200;
                if (status != 200) { ctx.Response.StatusCode = status; return; }
                ctx.Response.ContentType = "application/vnd.apple.mpegurl";
                await ctx.Response.WriteAsync(Live(poll));
                return;
            }
            var file = Path.Combine(root, path.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(file)) { ctx.Response.StatusCode = 404; return; }
            var ext = Path.GetExtension(file);
            ctx.Response.ContentType = ext switch
            {
                ".m3u8" => "application/vnd.apple.mpegurl", ".mpd" => "application/dash+xml", ".ts" => "video/mp2t",
                ".jpg" => "image/jpeg", ".vtt" => "text/vtt", _ => "application/octet-stream",
            };
            if (ctx.Request.Headers.Range.Count > 0)
            {
                var bytes = await File.ReadAllBytesAsync(file);
                var r = ctx.Request.Headers.Range[0]!["bytes=".Length..].Split('-');
                long from = long.Parse(r[0]), to = r.Length > 1 && r[1].Length > 0 ? long.Parse(r[1]) : bytes.Length - 1;
                to = Math.Min(to, bytes.Length - 1);
                ctx.Response.StatusCode = 206;
                ctx.Response.Headers.ContentRange = $"bytes {from}-{to}/{bytes.Length}";
                await ctx.Response.Body.WriteAsync(bytes.AsMemory((int)from, (int)(to - from + 1)));
                return;
            }
            ctx.Response.Headers.AcceptRanges = "bytes";
            await ctx.Response.SendFileAsync(file);
        });
        _app.StartAsync().GetAwaiter().GetResult();
        Base = new Uri(_app.Urls.First());
    }

    public Uri Url(string path) => new(Base, path);
    public ValueTask DisposeAsync() => _app.DisposeAsync();
}
