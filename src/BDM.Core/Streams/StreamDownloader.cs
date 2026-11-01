using System.Diagnostics;
using System.Globalization;
using System.Text;
using BDM.Core.Engine;
using BDM.Core.Files;
using BDM.Core.Http;

namespace BDM.Core.Streams;

/// <summary>
/// Downloads HLS and DASH streams: lists the qualities, fetches each track's segments (decrypting AES-128),
/// and remuxes with ffmpeg (-c copy). A live HLS stream is captured until it ends or the caller cancels.
/// </summary>
public sealed class StreamDownloader(HttpClient http, FfmpegLocator ffmpeg)
{
    static readonly StreamOptions Defaults = new();

    // ---------------------------------------------------------------- listing

    public async Task<IReadOnlyList<StreamChoice>> ListChoicesAsync(StreamSource s, CancellationToken ct)
    {
        var fetcher = new SegmentFetcher(http, s, Defaults);
        var text = s.ManifestBody ?? await fetcher.GetStringAsync(s.ManifestUrl, ct);
        return s.Kind == StreamKind.Hls ? ListHls(s, text) : ListDash(s, text);
    }

    static IReadOnlyList<StreamChoice> ListHls(StreamSource s, string text)
    {
        var pl = HlsParser.Parse(text, s.ManifestUrl);
        if (!pl.IsMaster) return [new StreamChoice("Original quality", new StreamTrack("media", s.ManifestUrl), null, [])];

        return pl.Variants.OrderByDescending(v => v.Bandwidth).Select(v =>
        {
            var audio = pl.Media.FirstOrDefault(m => m.Type == "AUDIO" && m.GroupId == v.AudioGroup && m.Url != null);
            var subs = pl.Media.Where(m => m.Type == "SUBTITLES" && m.GroupId == v.SubtitleGroup && m.Url != null)
                .Select(m => new StreamTrack(m.Language ?? m.Name ?? "sub", m.Url!)).ToList();
            return new StreamChoice($"{v.Resolution ?? "Unknown size"} ({v.Bandwidth / 1000} kbps)",
                new StreamTrack(v.Url.ToString(), v.Url), audio == null ? null : new StreamTrack(audio.Name ?? "audio", audio.Url!), subs);
        }).ToList();
    }

    static IReadOnlyList<StreamChoice> ListDash(StreamSource s, string text)
    {
        var m = DashParser.Parse(text, s.ManifestUrl);
        if (m.IsDynamic) throw new NotSupportedException("Live DASH streams are not supported yet.");
        if (m.Periods.Count != 1) throw new NotSupportedException("DASH streams with several periods are not supported yet.");
        var reps = m.Periods[0].Representations;
        var videos = reps.Where(r => r.MimeType.StartsWith("video/")).ToList();
        var audios = reps.Where(r => r.MimeType.StartsWith("audio/")).OrderByDescending(r => r.Bandwidth).ToList();
        if (reps.Count > 0 && reps.All(r => r.Protected) || videos.Count > 0 && videos.All(r => r.Protected))
            throw new ProtectedContentException("This stream is protected by DRM and cannot be downloaded.");

        StreamTrack Track(DashRepresentation r) => new(r.Id, s.ManifestUrl);
        var audio = audios.FirstOrDefault(a => !a.Protected);
        if (videos.Count == 0) return audios.Where(a => !a.Protected).Select(a => new StreamChoice($"Audio ({a.Bandwidth / 1000} kbps)", Track(a), null, [])).ToList();
        return videos.Where(v => !v.Protected).OrderByDescending(v => v.Height ?? 0).ThenByDescending(v => v.Bandwidth)
            .Select(v => new StreamChoice($"{v.Width}x{v.Height} ({v.Bandwidth / 1000} kbps)", Track(v), audio == null ? null : Track(audio), []))
            .ToList();
    }

    // ---------------------------------------------------------------- downloading

    public async Task<string> DownloadAsync(StreamSource s, StreamChoice choice, string destDir, IProgress<DownloadProgress>? progress,
        CancellationToken ct, StreamOptions? options = null)
    {
        var o = options ?? Defaults;
        var ff = ffmpeg.Locate() ?? throw new FfmpegMissingException(
            "ffmpeg was not found. Install ffmpeg or set its path in Options > Tools, then try again.");
        Directory.CreateDirectory(destDir);
        var work = Path.Combine(destDir, ".bdm-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(work);
        try
        {
            var fetcher = new SegmentFetcher(http, s, o);
            var meter = new Meter(progress);
            var downloads = new List<Task<TrackFile>> { DownloadTrackAsync(s, choice.Video, Path.Combine(work, "video"), o, fetcher, meter, 0, ct) };
            if (choice.Audio != null) downloads.Add(DownloadTrackAsync(s, choice.Audio, Path.Combine(work, "audio"), o, fetcher, meter, 1, ct));
            var tracks = await Task.WhenAll(downloads);
            var live = tracks.Any(t => t.WasLive);
            var mergeCt = live ? CancellationToken.None : ct; // a stopped live capture must still be finalized

            var baseName = FileNaming.Sanitize(string.IsNullOrWhiteSpace(s.Title) ? Path.GetFileNameWithoutExtension(s.ManifestUrl.AbsolutePath) : s.Title!);
            var output = Path.Combine(destDir, FileNaming.Unique(destDir, baseName + ".mp4"));
            var finalBase = Path.Combine(destDir, Path.GetFileNameWithoutExtension(output));

            var subtitleFiles = new List<string>();
            foreach (var sub in choice.Subtitles)
            {
                try { subtitleFiles.Add(await DownloadSubtitleAsync(sub, finalBase, fetcher, mergeCt)); }
                catch (Exception e) when (e is DownloadException or FormatException) { /* subtitles are optional */ }
            }

            var inputs = new List<string> { tracks[0].Path };
            if (tracks.Length > 1) inputs.Add(tracks[1].Path);
            var mergeSubs = o.MergeSubtitles ? subtitleFiles : new List<string>();
            var args = BuildMergeArgs(inputs, tracks.Length > 1, mergeSubs, output);
            await FfmpegRunner.RunAsync(ff, args, mergeCt);

            if (s.ThumbnailUrl != null)
            {
                try
                {
                    var ext = Path.GetExtension(s.ThumbnailUrl.AbsolutePath);
                    await File.WriteAllBytesAsync(finalBase + (ext.Length is >= 2 and <= 5 ? ext : ".jpg"),
                        await fetcher.GetBytesAsync(s.ThumbnailUrl, null, null, mergeCt), mergeCt);
                }
                catch (Exception e) when (e is DownloadException or IOException) { /* thumbnail is optional */ }
            }
            return output;
        }
        finally
        {
            try { Directory.Delete(work, true); } catch (IOException) { }
        }
    }

    /// <summary>
    /// ffmpeg arguments for the final remux. Only video and audio are mapped: "-map 0" would also pull timed-ID3, DVB subtitle
    /// and other data streams from .ts segments into the mp4 muxer, which cannot hold them.
    /// </summary>
    public static IReadOnlyList<string> BuildMergeArgs(IReadOnlyList<string> inputs, bool separateAudio, IReadOnlyList<string> subtitleFiles, string output)
    {
        var args = new List<string>();
        foreach (var i in inputs) { args.Add("-i"); args.Add(i); }
        foreach (var f in subtitleFiles) { args.Add("-i"); args.Add(f); }
        if (separateAudio) args.AddRange(["-map", "0:v", "-map", "1:a"]); else args.AddRange(["-map", "0:v?", "-map", "0:a?"]);
        for (var i = 0; i < subtitleFiles.Count; i++) { args.Add("-map"); args.Add((inputs.Count + i).ToString(CultureInfo.InvariantCulture)); }
        args.AddRange(["-c", "copy"]);
        if (subtitleFiles.Count > 0) args.AddRange(["-c:s", "mov_text"]);
        args.AddRange(["-movflags", "+faststart", output]);
        return args;
    }

    sealed record TrackFile(string Path, bool WasLive);

    /// <summary>Sums per-track byte counts into one progress stream.</summary>
    sealed class Meter(IProgress<DownloadProgress>? progress)
    {
        readonly long[] _bytes = new long[2];
        readonly Stopwatch _clock = Stopwatch.StartNew();
        public void Set(int track, long bytes)
        {
            Interlocked.Exchange(ref _bytes[track], bytes);
            var total = Interlocked.Read(ref _bytes[0]) + Interlocked.Read(ref _bytes[1]);
            progress?.Report(new DownloadProgress(total, null, total / Math.Max(0.1, _clock.Elapsed.TotalSeconds), []));
        }
    }

    Task<TrackFile> DownloadTrackAsync(StreamSource s, StreamTrack track, string outBase, StreamOptions o, SegmentFetcher f, Meter meter, int slot, CancellationToken ct) =>
        s.Kind == StreamKind.Hls
            ? DownloadHlsAsync(s, track.Playlist, outBase, o, f, meter, slot, ct)
            : DownloadDashAsync(s, track, outBase, o, f, meter, slot, ct);

    static void RequireSupported(HlsPlaylist pl)
    {
        if (pl.Segments.Any(x => x.Key is { IsSupported: false } k))
            throw new ProtectedContentException("This stream uses an encryption BDM cannot undo (SAMPLE-AES / DRM).");
    }

    async Task<TrackFile> DownloadHlsAsync(StreamSource s, Uri playlistUrl, string outBase, StreamOptions o, SegmentFetcher f, Meter meter, int slot, CancellationToken ct)
    {
        var text = playlistUrl == s.ManifestUrl && s.ManifestBody != null ? s.ManifestBody : await f.GetStringAsync(playlistUrl, ct);
        var pl = HlsParser.Parse(text, playlistUrl);
        if (pl.IsMaster) throw new FormatException("Expected a media playlist but got a master playlist.");
        RequireSupported(pl);
        var live = !pl.Ended;
        var path = outBase + (pl.Map != null ? ".mp4" : ".ts");
        long bytes = 0;
        var captured = 0; // media segments written so far

        await using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16, true);
        try
        {
            if (pl.Map != null)
            {
                var init = await f.GetBytesAsync(pl.Map.Url, pl.Map.ByteRangeOffset, pl.Map.ByteRangeLength, ct);
                await fs.WriteAsync(init, ct);
                bytes += init.Length;
            }
            var seen = new HashSet<long>();
            var idle = 0;
            while (true)
            {
                var fresh = pl.Segments.Where(x => seen.Add(x.Sequence)).ToList();
                foreach (var window in fresh.Chunk(Math.Max(1, o.Parallel)))
                {
                    var parts = await Task.WhenAll(window.Select(seg => f.GetSegmentAsync(seg, ct)));
                    foreach (var part in parts) { await fs.WriteAsync(part, ct); bytes += part.Length; captured++; }
                    meter.Set(slot, bytes);
                }
                if (!live || pl.Ended) break;
                idle = fresh.Count == 0 ? idle + 1 : 0;
                if (idle >= 20) break; // the server stopped adding segments without sending ENDLIST
                await Task.Delay(o.LivePollInterval ?? TimeSpan.FromSeconds(Math.Max(1, pl.TargetDuration / 2)), ct);
                pl = HlsParser.Parse(await f.GetStringAsync(playlistUrl, ct), playlistUrl);
                RequireSupported(pl);
            }
        }
        catch (OperationCanceledException) when (live)
        {
            // Stopping a live capture is the normal way to finish it: keep what was written.
        }
        catch (Exception e) when (live && captured > 0 && e is DownloadException or HttpRequestException)
        {
            // The playlist link expired or the server went away an hour into a live capture: finish with what we have rather than lose it.
            Log.Info($"Live capture ended by an error after {captured} segments: {e.Message}");
        }
        await fs.FlushAsync(CancellationToken.None);
        return new TrackFile(path, live);
    }

    async Task<TrackFile> DownloadDashAsync(StreamSource s, StreamTrack track, string outBase, StreamOptions o, SegmentFetcher f, Meter meter, int slot, CancellationToken ct)
    {
        var text = s.ManifestBody ?? await f.GetStringAsync(s.ManifestUrl, ct);
        var rep = DashParser.Parse(text, s.ManifestUrl).Periods[0].Representations.FirstOrDefault(r => r.Id == track.Id)
            ?? throw new FormatException($"Representation '{track.Id}' is not in the manifest any more.");
        if (rep.Protected) throw new ProtectedContentException("This stream is protected by DRM and cannot be downloaded.");
        var path = outBase + ".mp4";

        if (rep.InitUrl == null && rep.SegmentUrls.Count == 1)
        {
            // One self-contained file: use the multi-connection engine.
            var dir = Path.GetDirectoryName(path)!;
            var req = new DownloadRequest(rep.SegmentUrls[0], Path.GetFileName(path), s.Headers.GetValueOrDefault("Referer"),
                s.Headers.Where(h => h.Key != "Referer").ToDictionary(h => h.Key, h => h.Value), s.CookieHeader, null, null, dir);
            var probe = (await Prober.ProbeAsync(http, req, ct)) with { FileName = Path.GetFileName(path) };
            var result = await new SegmentedDownloader(http).RunAsync(req, probe, new DownloadOptions(MaxRetries: o.MaxRetries, RetryBaseDelay: o.RetryBaseDelay),
                new Adapter(p => meter.Set(slot, p.Downloaded)), ct);
            return new TrackFile(result.Path, false);
        }

        long bytes = 0;
        await using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16, true);
        if (rep.InitUrl != null)
        {
            var init = await f.GetBytesAsync(rep.InitUrl, null, null, ct);
            await fs.WriteAsync(init, ct);
            bytes += init.Length;
        }
        foreach (var window in rep.SegmentUrls.Chunk(Math.Max(1, o.Parallel)))
        {
            var parts = await Task.WhenAll(window.Select(u => f.GetBytesAsync(u, null, null, ct)));
            foreach (var part in parts) { await fs.WriteAsync(part, ct); bytes += part.Length; }
            meter.Set(slot, bytes);
        }
        return new TrackFile(path, false);
    }

    sealed class Adapter(Action<DownloadProgress> on) : IProgress<DownloadProgress> { public void Report(DownloadProgress v) => on(v); }

    /// <summary>WebVTT subtitle playlist → one .vtt file (segment headers dropped so WEBVTT appears once).</summary>
    async Task<string> DownloadSubtitleAsync(StreamTrack sub, string finalBase, SegmentFetcher f, CancellationToken ct)
    {
        var pl = HlsParser.Parse(await f.GetStringAsync(sub.Playlist, ct), sub.Playlist);
        var sb = new StringBuilder("WEBVTT\n\n");
        foreach (var seg in pl.Segments)
        {
            var lines = (await f.GetSegmentAsync(seg, ct) is var b ? Encoding.UTF8.GetString(b).TrimStart('﻿') : "").Replace("\r\n", "\n").Split('\n');
            var blank = Array.IndexOf(lines, "");
            if (blank >= 0) sb.AppendJoin('\n', lines.Skip(blank + 1)).Append('\n');
        }
        var path = $"{finalBase}.{FileNaming.Sanitize(sub.Id)}.vtt";
        await File.WriteAllTextAsync(path, sb.ToString(), new UTF8Encoding(false), ct);
        return path;
    }
}
