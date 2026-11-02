using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using BDM.Core.Files;
using BDM.Core.Http;
using Microsoft.Win32.SafeHandles;

namespace BDM.Core.Engine;

/// <param name="limiter">The budget shared by every download (the global limit).</param>
/// <param name="own">This download's own limit, applied on top of the shared one.</param>
public sealed class SegmentedDownloader(HttpClient http, SpeedLimiter? limiter = null, SpeedLimiter? own = null)
{
    static readonly TimeSpan Stall = TimeSpan.FromSeconds(30);

    /// <summary>The server did not honor a Range request (200, or a different range): fall back to a clean full download.</summary>
    sealed class RangeIgnoredException : Exception;

    /// <summary>The server refused one more connection (503/429) while another is receiving: this one steps aside.</summary>
    sealed class ServerFullException : Exception;

    /// <param name="partKey">Makes the .bdmpart name unique per download, so same-named downloads in one folder never share partial data.</param>
    public async Task<DownloadResult> RunAsync(DownloadRequest req, FileProbe probe, DownloadOptions opts,
        IProgress<DownloadProgress>? progress, CancellationToken ct, string? partKey = null)
    {
        Directory.CreateDirectory(req.DestinationDir);
        var part = Path.Combine(req.DestinationDir, probe.FileName + (partKey == null ? "" : "." + partKey) + ".bdmpart");
        var side = part + ".json";
        var lim = limiter ?? (opts.SpeedLimitBytesPerSec is > 0 ? new SpeedLimiter(opts.SpeedLimitBytesPerSec.Value) : null);

        if (probe.Length is not > 0 || !probe.AcceptsRanges)
            return await SingleAsync(req, probe, opts, lim, part, side, progress, ct);
        try
        {
            return await SegmentedAsync(req, probe, opts, lim, part, side, progress, ct);
        }
        catch (RangeIgnoredException)
        {
            return await SingleAsync(req, probe, opts, lim, part, side, progress, ct);
        }
    }

    static bool Matches(string? saved, string? current) => saved == null || current == null || saved == current;

    async Task<DownloadResult> SegmentedAsync(DownloadRequest req, FileProbe probe, DownloadOptions opts, SpeedLimiter? lim,
        string part, string side, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        var length = probe.Length!.Value;
        SegmentMap map;
        var loaded = File.Exists(part) ? SegmentSidecar.Load(side) : null;
        if (loaded is { } l && l.Map.Length == length && Matches(l.ETag, probe.ETag) && Matches(l.LastModified, probe.LastModified))
            map = l.Map;
        else
        {
            Delete(part, side);
            map = SegmentMap.Create(length, opts.Connections);
            try { using var f = File.Create(part); f.SetLength(length); }
            catch (IOException e) { throw DownloadException.FromIo(e); }
        }

        using (var handle = File.OpenHandle(part, FileMode.Open, FileAccess.Write, FileShare.ReadWrite, FileOptions.Asynchronous))
        {
            using var work = CancellationTokenSource.CreateLinkedTokenSource(ct);
            using var stopReporter = new CancellationTokenSource();
            var claimed = new HashSet<Segment>();
            var claimLock = new object();
            var receiving = new System.Runtime.CompilerServices.StrongBox<int>();
            Exception? fault = null;

            Segment? Claim()
            {
                lock (claimLock)
                {
                    foreach (var s in map.Snapshot())
                        if (s.Remaining > 0 && claimed.Add(s)) return s;
                    if (map.TrySplitLargest(opts.MinSplitBytes, out var created)) { claimed.Add(created); return created; }
                    return null;
                }
            }

            async Task Worker()
            {
                try
                {
                    while (!work.IsCancellationRequested && Claim() is { } seg)
                    {
                        try { await RunSegmentAsync(req, probe, opts, seg, handle, lim, receiving, work.Token); }
                        catch (ServerFullException)
                        {
                            // Free hosts (Hotlink) allow one connection: hand the part back for a working connection to take later.
                            lock (claimLock) claimed.Remove(seg);
                            return;
                        }
                    }
                }
                catch (Exception e) when (e is not OperationCanceledException || !work.IsCancellationRequested)
                {
                    Interlocked.CompareExchange(ref fault, e, null);
                    work.Cancel();
                }
                catch (OperationCanceledException) { }
            }

            var reporter = Task.Run(() => ReportLoop(map, length, side, probe, progress, stopReporter.Token));
            try
            {
                await Task.WhenAll(Enumerable.Range(0, Math.Max(1, opts.Connections)).Select(_ => Task.Run(Worker)));
            }
            finally
            {
                stopReporter.Cancel();
                await reporter;
                if (!map.IsComplete) SegmentSidecar.Save(side, map, probe.ETag, probe.LastModified);
            }
            ct.ThrowIfCancellationRequested();
            if (fault != null) throw fault is DownloadException or RangeIgnoredException ? fault : new DownloadException(DownloadFailure.Network, fault.Message, fault);
            Report(progress, Snapshot(map, length, 0));
        }

        return Finish(req, probe, part, side);
    }

    async Task ReportLoop(SegmentMap map, long length, string side, FileProbe probe, IProgress<DownloadProgress>? progress, CancellationToken stop)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(200));
        long last = 0;
        var clock = Stopwatch.StartNew();
        var ticks = 0;
        try
        {
            while (await timer.WaitForNextTickAsync(stop))
            {
                var snap = Snapshot(map, length, 0);
                var dt = clock.Elapsed.TotalSeconds;
                clock.Restart();
                var speed = dt > 0 ? (snap.Downloaded - last) / dt : 0;
                last = snap.Downloaded;
                Report(progress, snap with { BytesPerSec = speed });
                if (++ticks % 5 == 0)
                {
                    // A scanner or indexer briefly holding the sidecar must not fail a download that is otherwise fine.
                    try { SegmentSidecar.Save(side, map, probe.ETag, probe.LastModified); }
                    catch (IOException e) { Log.Error("Could not save the resume file", e); }
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>A broken progress listener (a window that went away) must never fail the download it is watching.</summary>
    static void Report(IProgress<DownloadProgress>? progress, DownloadProgress value)
    {
        try { progress?.Report(value); }
        catch (Exception e) when (e is not OperationCanceledException) { Log.Error("A progress listener failed", e); }
    }

    static DownloadProgress Snapshot(SegmentMap map, long length, double speed)
    {
        var segs = map.Snapshot();
        var done = segs.Sum(s => Math.Min(Interlocked.Read(ref s.Downloaded), s.End - s.Start + 1));
        return new DownloadProgress(done, length, speed, segs);
    }

    async Task RunSegmentAsync(DownloadRequest req, FileProbe probe, DownloadOptions opts, Segment seg,
        SafeFileHandle handle, SpeedLimiter? lim, System.Runtime.CompilerServices.StrongBox<int> receiving, CancellationToken ct)
    {
        var attempt = 0;
        var baseDelay = opts.RetryBaseDelay ?? TimeSpan.FromMilliseconds(500);
        while (seg.Remaining > 0)
        {
            var before = Interlocked.Read(ref seg.Downloaded);
            try
            {
                await FetchAsync(req, probe, seg, handle, lim, receiving, ct);
            }
            catch (HttpRequestException e) when (e.StatusCode is HttpStatusCode.ServiceUnavailable or HttpStatusCode.TooManyRequests
                                                 && Volatile.Read(ref receiving.Value) > 0 && !ct.IsCancellationRequested)
            {
                throw new ServerFullException();
            }
            catch (Exception e) when ((e is HttpRequestException or IOException or OperationCanceledException) && !ct.IsCancellationRequested)
            {
                if (Interlocked.Read(ref seg.Downloaded) > before) attempt = 0;
                if (++attempt > opts.MaxRetries) throw new DownloadException(DownloadFailure.Network, "Giving up after retries: " + e.Message, e);
                await Task.Delay(TimeSpan.FromMilliseconds(baseDelay.TotalMilliseconds * Math.Pow(2, attempt - 1)), ct);
            }
        }
    }

    async Task FetchAsync(DownloadRequest req, FileProbe probe, Segment seg, SafeFileHandle handle, SpeedLimiter? lim,
        System.Runtime.CompilerServices.StrongBox<int> receiving, CancellationToken ct)
    {
        var from = seg.Start + Interlocked.Read(ref seg.Downloaded);
        using var msg = req.CreateMessage(HttpMethod.Get);
        msg.Headers.Range = new RangeHeaderValue(from, seg.End);
        if (probe.ETag is { } etag && !etag.StartsWith("W/")) msg.Headers.TryAddWithoutValidation("If-Range", etag);
        using var resp = await http.SendAsync(msg, HttpCompletionOption.ResponseHeadersRead, ct);
        CheckStatus(resp);
        if (resp.StatusCode != HttpStatusCode.PartialContent || resp.Content.Headers.ContentRange?.From != from)
            throw new RangeIgnoredException();

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        var buf = ArrayPool<byte>.Shared.Rent(64 * 1024);
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Interlocked.Increment(ref receiving.Value);
        try
        {
            while (true)
            {
                var remaining = seg.End - seg.Start + 1 - Interlocked.Read(ref seg.Downloaded);
                if (remaining <= 0) return;
                stall.CancelAfter(Stall);
                var n = await stream.ReadAsync(buf.AsMemory(0, (int)Math.Min(buf.Length, remaining)), stall.Token);
                if (n == 0) throw new IOException("Connection closed before the segment was complete.");
                if (lim != null) await lim.AcquireAsync(n, ct);
                if (own != null) await own.AcquireAsync(n, ct);
                try { await RandomAccess.WriteAsync(handle, buf.AsMemory(0, n), seg.Start + Interlocked.Read(ref seg.Downloaded), ct); }
                catch (IOException e) { throw DownloadException.FromIo(e); }
                Interlocked.Add(ref seg.Downloaded, n);
            }
        }
        finally { Interlocked.Decrement(ref receiving.Value); ArrayPool<byte>.Shared.Return(buf); }
    }

    static void CheckStatus(HttpResponseMessage resp)
    {
        var code = (int)resp.StatusCode;
        // 204/205 carry no file: accepting them "finished" downloads as empty files.
        if (code is 204 or 205) throw new HttpRequestException($"The server answered {code} (no content) instead of the file.", null, resp.StatusCode);
        if (resp.IsSuccessStatusCode) return;
        if (code is 401 or 403) throw new DownloadException(DownloadFailure.AuthExpired, $"The server refused access ({code}); the link may have expired.");
        if (code == 416) throw new DownloadException(DownloadFailure.ServerChanged, "The file changed on the server.");
        if (code >= 500 || code is 408 or 429) throw new HttpRequestException($"Server error {code}", null, resp.StatusCode);
        throw new DownloadException(DownloadFailure.Network, $"The server answered {code}.");
    }

    /// <summary>Plain one-connection download (no ranges, or the server ignored them). Restarts from zero on a transient failure.</summary>
    async Task<DownloadResult> SingleAsync(DownloadRequest req, FileProbe probe, DownloadOptions opts, SpeedLimiter? lim,
        string part, string side, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        Delete(part, side);
        var baseDelay = opts.RetryBaseDelay ?? TimeSpan.FromMilliseconds(500);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var msg = req.CreateMessage(HttpMethod.Get);
                using var resp = await http.SendAsync(msg, HttpCompletionOption.ResponseHeadersRead, ct);
                CheckStatus(resp);
                var total = resp.Content.Headers.ContentLength ?? probe.Length;
                await using var stream = await resp.Content.ReadAsStreamAsync(ct);
                await using (var fs = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16, true))
                {
                    var buf = new byte[64 * 1024];
                    long done = 0, window = 0;
                    var clock = Stopwatch.StartNew();
                    var seg = new[] { new Segment { Start = 0, End = Math.Max(0, (total ?? 1) - 1) } };
                    int n;
                    while ((n = await stream.ReadAsync(buf, ct)) > 0)
                    {
                        if (lim != null) await lim.AcquireAsync(n, ct);
                        if (own != null) await own.AcquireAsync(n, ct);
                        await fs.WriteAsync(buf.AsMemory(0, n), ct);
                        done += n;
                        window += n;
                        seg[0].Downloaded = done;
                        if (clock.ElapsedMilliseconds >= 200)
                        {
                            Report(progress, new DownloadProgress(done, total, window / clock.Elapsed.TotalSeconds, seg));
                            clock.Restart();
                            window = 0;
                        }
                    }
                    Report(progress, new DownloadProgress(done, total, 0, seg));
                }
                return Finish(req, probe, part, side);
            }
            catch (IOException e) when (e.HResult is unchecked((int)0x80070070) or unchecked((int)0x80070027))
            {
                throw DownloadException.FromIo(e);
            }
            catch (Exception e) when ((e is HttpRequestException or IOException) && !ct.IsCancellationRequested)
            {
                if (attempt > opts.MaxRetries) throw new DownloadException(DownloadFailure.Network, "Giving up after retries: " + e.Message, e);
                await Task.Delay(TimeSpan.FromMilliseconds(baseDelay.TotalMilliseconds * Math.Pow(2, attempt - 1)), ct);
            }
        }
    }

    static DownloadResult Finish(DownloadRequest req, FileProbe probe, string part, string side)
    {
        var final = FileNaming.MoveUnique(part, req.DestinationDir, probe.FileName);
        if (File.Exists(side)) File.Delete(side);
        return new DownloadResult(final, new FileInfo(final).Length);
    }

    static void Delete(params string[] paths)
    {
        foreach (var p in paths) if (File.Exists(p)) File.Delete(p);
    }
}
