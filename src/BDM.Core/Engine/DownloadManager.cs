using System.Security.Cryptography;
using BDM.Core.Data;
using BDM.Core.Files;
using BDM.Core.Ext;
using BDM.Core.Ftp;
using BDM.Core.Http;
using BDM.Core.Net;
using BDM.Core.Streams;

namespace BDM.Core.Engine;

/// <summary>Owns all downloads: persistence, queue concurrency, pause/resume and completion checks.</summary>
public class DownloadManager
{
    readonly DownloadStore _store;
    readonly Func<string?, HttpClient> _httpFor;
    readonly object _gate = new();
    readonly Dictionary<Guid, DownloadRecord> _records;
    readonly Dictionary<Guid, CancellationTokenSource> _running = new();
    readonly Dictionary<Guid, DateTime> _lastPersist = new();
    readonly Dictionary<Guid, IReadOnlyList<Segment>> _segments = new();
    readonly Dictionary<Guid, SpeedLimiter> _ownLimits = new();

    readonly HashSet<Guid> _windowStopped = new();
    readonly HashSet<string> _batchDone = new();
    readonly Dictionary<string, string> _lastCompleted = new();
    readonly Timer _tick;

    public DownloadOptions BaseOptions { get; set; } = new();
    /// <summary>Needed for stream (HLS/DASH) jobs; set by the app once ffmpeg handling is configured.</summary>
    public StreamDownloader? Streams { get; set; }
    public StreamOptions StreamOptions { get; set; } = new();
    /// <summary>Builds a stream downloader around the (possibly proxied) HttpClient; overrides <see cref="Streams"/> when set.</summary>
    public Func<HttpClient, StreamDownloader>? StreamsFor { get; set; }
    /// <summary>Category name -> proxy profile id.</summary>
    public Dictionary<string, string> CategoryProxies { get; } = new();
    public string? GlobalProxyId { get; set; }
    /// <summary>One byte budget shared by every segmented download (SetLimit(0) = unlimited). Per-download limits still apply on top.</summary>
    public SpeedLimiter? GlobalLimiter { get; set; }
    public SiteLogins? Logins { get; set; }
    /// <summary>yt-dlp for video pages BDM can't read directly (null = not set up).</summary>
    public YtDlpRunner? YtDlp { get; set; }
    /// <summary>Turns a proxy profile id into a proxy URL for yt-dlp (it runs outside our HttpClient).</summary>
    public Func<string?, string?>? ProxyUrlFor { get; set; }
    /// <summary>Called (fire and forget, exceptions swallowed) after a download completes: antivirus scan, notifications...</summary>
    public Func<DownloadRecord, Task>? OnCompleted { get; set; }
    public Func<DateTime> Clock { get; set; } = () => DateTime.Now;
    public IPowerApi PowerApi { get; set; } = new WindowsPowerApi();
    /// <summary>Program launched by the RunProgram post-action.</summary>
    public string? PostProgram { get; set; }
    public event Action<DownloadRecord>? Changed;

    public DownloadManager(DownloadStore store, Func<string?, HttpClient> httpFor)
    {
        _store = store;
        _httpFor = httpFor;
        _tick = new Timer(_ => Tick(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        _records = store.All().ToDictionary(r => r.Id);
        // A crash can leave Running rows behind: nothing is running now, so park them as Paused.
        foreach (var r in _records.Values.Where(r => r.State == DownloadState.Running).ToList())
        {
            _records[r.Id] = r with { State = DownloadState.Paused };
            store.Upsert(_records[r.Id]);
        }
    }

    public DownloadRecord? Get(Guid id) { lock (_gate) return _records.GetValueOrDefault(id); }

    /// <summary>Live segment map of a running segmented download (for the progress window), else null.</summary>
    public IReadOnlyList<Segment>? SegmentsFor(Guid id) { lock (_gate) return _segments.GetValueOrDefault(id); }

    public IReadOnlyList<DownloadRecord> All() { lock (_gate) return _records.Values.OrderBy(r => r.Added).ToList(); }

    /// <summary>Short per-download tag in the .bdmpart names, so same-named downloads in one folder never share partial data.</summary>
    static string PartKey(Guid id) => id.ToString("N")[..8];

    /// <summary>
    /// A fresh link for a download that failed because its link expired continues that download (keeping the bytes it has)
    /// instead of starting a duplicate from zero. Matches on the same file in the same folder, ignoring signature parameters.
    /// </summary>
    Guid? TryReattach(DownloadRequest request, StreamSource? stream, StreamChoice? choice)
    {
        DownloadRecord updated;
        lock (_gate)
        {
            var key = UrlKey.Normalize(request.Url);
            var prior = _records.Values.FirstOrDefault(x => x is { State: DownloadState.Failed, Failure: DownloadFailure.AuthExpired, YtDlpFormat: null }
                && (x.Stream != null) == (stream != null)
                && x.Request.DestinationDir == request.DestinationDir && UrlKey.Normalize(x.Request.Url) == key);
            if (prior == null) return null;
            updated = prior with { Request = request, Stream = stream ?? prior.Stream, StreamChoice = choice ?? prior.StreamChoice, State = DownloadState.Queued, Error = null, Failure = null };
            _records[prior.Id] = updated;
        }
        _store.Upsert(updated);
        Changed?.Invoke(updated);
        Pump(updated.QueueId);
        return updated.Id;
    }

    public Guid Add(DownloadRequest request, string queueId = "main", string? expectedSha256 = null)
    {
        if (TryReattach(request, null, null) is { } existing) return existing;
        DownloadRecord rec;
        lock (_gate)
        {
            var dup = _records.Values.Any(x => x.Request.Url == request.Url);
            rec = new DownloadRecord(Guid.NewGuid(), request, DownloadState.Queued, 0, null, null, queueId, DateTime.UtcNow,
                Duplicate: dup, ExpectedSha256: expectedSha256);
            _records[rec.Id] = rec;
        }
        _store.Upsert(rec);
        Changed?.Invoke(rec);
        Pump(queueId);
        return rec.Id;
    }

    /// <summary>Queues an HLS/DASH download. The request URL is the manifest; the finished file is a remuxed .mp4.</summary>
    public Guid AddStream(StreamSource source, StreamChoice choice, string destinationDir, string queueId = "main")
    {
        var request = new DownloadRequest(source.ManifestUrl, null, source.Headers.GetValueOrDefault("Referer"),
            source.Headers.Where(h => h.Key != "Referer").ToDictionary(h => h.Key, h => h.Value), source.CookieHeader, null, null, destinationDir);
        if (TryReattach(request, source, choice) is { } existing) return existing;
        DownloadRecord rec;
        lock (_gate)
        {
            rec = new DownloadRecord(Guid.NewGuid(), request, DownloadState.Queued, 0, null, null, queueId, DateTime.UtcNow,
                Duplicate: _records.Values.Any(x => x.Request.Url == request.Url), Stream: source, StreamChoice: choice);
            _records[rec.Id] = rec;
        }
        _store.Upsert(rec);
        Changed?.Invoke(rec);
        Pump(queueId);
        return rec.Id;
    }

    /// <summary>Adopts downloads that were written straight into the store (a settings import). Returns how many were new.</summary>
    public int ReloadFromStore()
    {
        var added = new List<DownloadRecord>();
        lock (_gate)
        {
            foreach (var r in _store.All().Where(r => !_records.ContainsKey(r.Id)))
            {
                var adopted = r.State == DownloadState.Running ? r with { State = DownloadState.Paused } : r;
                _records[r.Id] = adopted;
                added.Add(adopted);
            }
        }
        foreach (var r in added) { _store.Upsert(r); Changed?.Invoke(r); }
        return added.Count;
    }

    /// <summary>Queues a video-page download handled by yt-dlp.</summary>
    public Guid AddYtDlp(Uri pageUrl, StreamChoice choice, string destinationDir, IReadOnlyDictionary<string, string>? headers, string? cookie, string queueId = "main")
    {
        var other = (headers ?? new Dictionary<string, string>()).Where(h => !h.Key.Equals("Referer", StringComparison.OrdinalIgnoreCase) && !h.Key.Equals("Cookie", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(h => h.Key, h => h.Value);
        var referer = headers?.FirstOrDefault(h => h.Key.Equals("Referer", StringComparison.OrdinalIgnoreCase)).Value;
        var request = new DownloadRequest(pageUrl, null, referer, other, cookie, null, "Video", destinationDir);
        DownloadRecord rec;
        lock (_gate)
        {
            rec = new DownloadRecord(Guid.NewGuid(), request, DownloadState.Queued, 0, null, null, queueId, DateTime.UtcNow,
                Duplicate: _records.Values.Any(x => x.Request.Url == pageUrl), YtDlpFormat: choice.Video.Id);
            _records[rec.Id] = rec;
        }
        _store.Upsert(rec);
        Changed?.Invoke(rec);
        Pump(queueId);
        return rec.Id;
    }

    /// <summary>JSON dump of every download and queue (settings export / backup).</summary>
    public string ExportAll() => _store.Export();

    /// <summary>Proxy profile for a download: the download itself, then its queue, then its category, then the global default.</summary>
    public string? ProxyIdFor(string? category, string queueId, string? downloadProfile = null)
    {
        var q = _store.Queue(queueId) ?? QueueDefinition.Main;
        return downloadProfile ?? q.ProxyProfileId
            ?? (category != null && CategoryProxies.TryGetValue(category, out var c) ? c : null)
            ?? GlobalProxyId;
    }

    public StreamDownloader? StreamDownloaderFor(string? proxyId) => StreamsFor != null ? StreamsFor(_httpFor(proxyId)) : Streams;

    void FireCompleted(Guid id)
    {
        var hook = OnCompleted;
        if (hook == null) return;
        _ = Task.Run(async () =>
        {
            try { if (Get(id) is { } rec) await hook(rec); }
            catch (Exception e) { Log.Error("A completion hook failed", e); } // a failing scanner must not affect the download
        });
    }

    public void Pause(Guid id)
    {
        CancellationTokenSource? cts;
        lock (_gate)
        {
            if (!_records.TryGetValue(id, out var r) || r.State is not (DownloadState.Queued or DownloadState.Running)) return;
            _running.TryGetValue(id, out cts);
        }
        Update(id, r => r with { State = DownloadState.Paused }, persist: true);
        cts?.Cancel();
    }

    /// <summary>Sets (null = removes) a download's own speed limit; a running download slows down or speeds up at once.</summary>
    public void SetSpeedLimit(Guid id, long? bytesPerSec)
    {
        var limit = bytesPerSec is > 0 ? bytesPerSec : null;
        Update(id, r => r with { Request = r.Request with { SpeedLimitBytesPerSec = limit } }, persist: true);
        lock (_gate) if (_ownLimits.TryGetValue(id, out var running)) running.SetLimit(limit ?? 0);
    }

    public void Resume(Guid id)
    {
        DownloadRecord? r = Get(id);
        if (r is not { State: DownloadState.Paused or DownloadState.Failed }) return;
        Update(id, x => x with { State = DownloadState.Queued, Error = null, Failure = null }, persist: true);
        Pump(r.QueueId);
    }

    public void Remove(Guid id, bool deleteFile)
    {
        DownloadRecord? r;
        CancellationTokenSource? cts;
        lock (_gate)
        {
            if (!_records.Remove(id, out r)) return;
            _running.TryGetValue(id, out cts);
        }
        cts?.Cancel();
        _store.Delete(id);
        if (!deleteFile) return;

        // FilePath of an unfinished download is only the name it is expected to get; a file with that name may belong to someone else.
        if (r.State == DownloadState.Completed && r.FilePath != null) TryDelete(r.FilePath);
        DeletePartFiles(r.Request.DestinationDir, PartKey(id));
        if (cts == null) return;
        // The cancelled run is still unwinding and may write its sidecar once more: clean up again when it has stopped.
        _ = Task.Run(async () =>
        {
            for (var i = 0; i < 100; i++)
            {
                lock (_gate) if (!_running.ContainsKey(id)) break;
                await Task.Delay(100);
            }
            DeletePartFiles(r.Request.DestinationDir, PartKey(id));
        });
    }

    static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log.Error("Could not delete " + path, e); }
    }

    static void DeletePartFiles(string dir, string key)
    {
        if (!Directory.Exists(dir)) return;
        foreach (var p in Directory.GetFiles(dir, $"*.{key}.bdmpart*")) TryDelete(p);
    }

    /// <summary>Re-queues downloads that failed for network reasons (called when connectivity returns).</summary>
    public void ResumeNetworkFailures()
    {
        foreach (var r in All().Where(r => r.State == DownloadState.Failed && r.Failure == DownloadFailure.Network))
            Resume(r.Id);
    }

    public void Watch(ConnectivityWatcher watcher) => watcher.NetworkRestored += ResumeNetworkFailures;

    /// <summary>Applies queue schedules: stops running downloads of closed queues, starts queued ones of open queues.</summary>
    public void Tick()
    {
        foreach (var q in _store.Queues())
        {
            if (Scheduler.IsWindowOpen(q, Clock())) { Pump(q.Id); continue; }
            List<CancellationTokenSource> stop = new();
            lock (_gate)
                foreach (var r in _records.Values.Where(r => r.QueueId == q.Id && r.State == DownloadState.Running))
                    if (_running.TryGetValue(r.Id, out var cts)) { _windowStopped.Add(r.Id); stop.Add(cts); }
            foreach (var cts in stop) cts.Cancel();
        }
    }

    void CheckQueueFinished(QueueDefinition q)
    {
        string? path;
        lock (_gate)
        {
            if (_records.Values.Any(r => r.QueueId == q.Id && r.State is DownloadState.Queued or DownloadState.Running)) return;
            if (!_batchDone.Remove(q.Id)) return;
            path = _lastCompleted.GetValueOrDefault(q.Id);
        }
        PostActions.Run(q.OnFinish, path, PostProgram, PowerApi);
    }

    /// <summary>Starts queued downloads of a queue until its concurrency limit is reached.</summary>
    void Pump(string queueId)
    {
        var q = _store.Queue(queueId) ?? QueueDefinition.Main;
        if (!Scheduler.IsWindowOpen(q, Clock())) return;
        var toStart = new List<(DownloadRecord Rec, CancellationTokenSource Cts)>();
        lock (_gate)
        {
            var running = _records.Values.Count(r => r.QueueId == queueId && r.State == DownloadState.Running);
            // A paused-then-resumed download is Queued again while its cancelled run is still unwinding: wait for that run to end
            // (its finally pumps again) rather than start a second one on the same files.
            foreach (var next in _records.Values.Where(r => r.QueueId == queueId && r.State == DownloadState.Queued && !_running.ContainsKey(r.Id)).OrderBy(r => r.Added))
            {
                if (running >= q.MaxConcurrent) break;
                var started = next with { State = DownloadState.Running };
                _records[next.Id] = started;
                // Registered here, in the same step as the state change, so Pause() always finds the token to cancel.
                var cts = new CancellationTokenSource();
                _running[next.Id] = cts;
                toStart.Add((started, cts));
                running++;
            }
        }
        foreach (var (r, cts) in toStart)
        {
            _store.Upsert(r);
            Changed?.Invoke(r);
            _ = Task.Run(() => RunAsync(r.Id, q, cts));
        }
    }

    DownloadRecord? Update(Guid id, Func<DownloadRecord, DownloadRecord> change, bool persist)
    {
        DownloadRecord updated;
        lock (_gate)
        {
            if (!_records.TryGetValue(id, out var cur)) return null;
            updated = change(cur);
            _records[id] = updated;
            if (!persist && DateTime.UtcNow - _lastPersist.GetValueOrDefault(id) > TimeSpan.FromSeconds(1)) persist = true;
            if (persist) _lastPersist[id] = DateTime.UtcNow;
        }
        if (persist) _store.Upsert(updated);
        Changed?.Invoke(updated);
        return updated;
    }

    /// <summary>Adds Basic auth from the saved site logins unless the request already carries an Authorization header.</summary>
    DownloadRequest WithLogin(DownloadRequest r)
    {
        if (Logins?.AuthorizationFor(r.Url) is not { } auth || r.Headers.Keys.Any(k => k.Equals("Authorization", StringComparison.OrdinalIgnoreCase))) return r;
        return r with { Headers = new Dictionary<string, string>(r.Headers) { ["Authorization"] = auth } };
    }

    sealed class SyncProgress(Action<DownloadProgress> on) : IProgress<DownloadProgress>
    {
        public void Report(DownloadProgress v) => on(v);
    }

    /// <summary>Shared finish for every kind of download: Mark-of-the-Web, batch flag, Completed state without session secrets.</summary>
    void Complete(Guid id, QueueDefinition q, string path, long bytes)
    {
        if (Get(id) is { } rec) ZoneIdentifier.Mark(path, rec.Request.Url, rec.Request.Referer);
        // Mark the batch BEFORE the state flips: a sibling's finish-check must never see this download as
        // done without also seeing the flag, or the post-action could fire twice.
        lock (_gate) { _batchDone.Add(q.Id); _lastCompleted[q.Id] = path; }
        Update(id, r => (r with { State = DownloadState.Completed, Downloaded = bytes, Total = bytes, FilePath = path, Error = null }).WithoutSecrets(), persist: true);
        FireCompleted(id);
    }

    void Fail(Guid id, DownloadFailure kind, Exception e)
    {
        Log.Error($"Download {id} failed ({kind})", e);
        Update(id, r => r.State == DownloadState.Running ? r with { State = DownloadState.Failed, Failure = kind, Error = e.Message } : r, persist: true);
    }

    static bool IsDiskFull(IOException e) => e.HResult is unchecked((int)0x80070070) or unchecked((int)0x80070027);

    // yt-dlp reports a dropped connection and a bad URL the same way (exit 1 + an ERROR line): only the first is worth retrying.
    static readonly System.Text.RegularExpressions.Regex NetworkWords = new("timed out|connection|network|unreachable|temporary failure|name resolution|unable to download", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    async Task RunAsync(Guid id, QueueDefinition q, CancellationTokenSource cts)
    {
        var ct = cts.Token;
        try
        {
            var rec = Get(id)!;
            if (rec.YtDlpFormat != null)
            {
                var yt = YtDlp ?? throw new YtDlpMissingException("yt-dlp is not set up. Set its path in Options > Tools, then resume.");
                var headers = rec.Request.Headers.ToDictionary(h => h.Key, h => h.Value);
                if (rec.Request.Referer != null) headers["Referer"] = rec.Request.Referer;
                if (rec.Request.CookieHeader != null) headers["Cookie"] = rec.Request.CookieHeader;
                var proxyUrl = ProxyUrlFor?.Invoke(ProxyIdFor(rec.Request.Category ?? "Video", q.Id, rec.Request.ProxyProfileId));
                var file = await yt.DownloadAsync(rec.Request.Url, rec.YtDlpFormat, rec.Request.DestinationDir, headers,
                    new SyncProgress(p => Update(id, r => r with { Downloaded = p.Downloaded, Total = p.Total ?? r.Total }, persist: false)), ct, proxyUrl);
                Complete(id, q, file, new FileInfo(file).Length);
                return;
            }
            if (rec.Stream != null)
            {
                var streams = StreamDownloaderFor(ProxyIdFor(rec.Request.Category ?? "Video", q.Id, rec.Request.ProxyProfileId))
                    ?? throw new InvalidOperationException("Stream downloads are not configured.");
                var path = await streams.DownloadAsync(rec.Stream, rec.StreamChoice!, rec.Request.DestinationDir,
                    new SyncProgress(p => Update(id, r => r with { Downloaded = p.Downloaded }, persist: false)), ct, StreamOptions);
                Complete(id, q, path, new FileInfo(path).Length);
                return;
            }
            var req = WithLogin(rec.Request);
            var http = _httpFor(ProxyIdFor(req.Category, q.Id, req.ProxyProfileId));
            var progress = new SyncProgress(p =>
            {
                lock (_gate) { if (p.Segments.Count > 0) _segments[id] = p.Segments; }
                Update(id, r => r with { Downloaded = p.Downloaded, Total = p.Total ?? r.Total }, persist: false);
            });
            DownloadResult result;
            if (req.Url.Scheme is "ftp" or "ftps")
            {
                result = await new FtpDownloader().RunAsync(req, progress, ct, PartKey(id));
            }
            else
            {
                var probe = await Prober.ProbeAsync(http, req, ct);
                Update(id, r => r with { Total = probe.Length, FilePath = Path.Combine(r.Request.DestinationDir, probe.FileName) }, persist: true);
                var opts = BaseOptions with { Connections = req.Connections ?? q.Connections };
                var own = new SpeedLimiter(req.SpeedLimitBytesPerSec ?? 0);
                lock (_gate) _ownLimits[id] = own;
                result = await new SegmentedDownloader(http, GlobalLimiter, own).RunAsync(req, probe, opts, progress, ct, PartKey(id));
            }

            if (rec.ExpectedSha256 is { } want &&
                !string.Equals(await Checksum.ComputeAsync(result.Path, HashAlgorithmName.SHA256), want, StringComparison.OrdinalIgnoreCase))
            {
                Update(id, r => r with { State = DownloadState.Failed, Failure = DownloadFailure.ServerChanged, Error = "Checksum mismatch: the file does not match the expected SHA-256.", FilePath = result.Path }, persist: true);
                return;
            }
            Complete(id, q, result.Path, result.Bytes);
        }
        catch (OperationCanceledException)
        {
            // Pause() sets Paused before cancelling; Remove() deletes the row; a closing schedule window re-queues.
            // Anything else (shutdown) parks it as Paused.
            bool windowStop;
            lock (_gate) windowStop = _windowStopped.Remove(id);
            Update(id, r => r.State == DownloadState.Running ? r with { State = windowStop ? DownloadState.Queued : DownloadState.Paused } : r, persist: true);
        }
        catch (ProtectedContentException e) { Fail(id, DownloadFailure.Protected, e); }
        catch (DownloadException e) { Fail(id, e.Kind, e); }
        catch (IOException e) { Fail(id, IsDiskFull(e) ? DownloadFailure.DiskFull : DownloadFailure.Other, e); }
        catch (HttpRequestException e) { Fail(id, DownloadFailure.Network, e); }
        // Only genuine network trouble is retried when the connection returns; a bad URL, a missing tool or a deleted proxy needs the owner.
        catch (YtDlpException e) { Fail(id, NetworkWords.IsMatch(e.Message) ? DownloadFailure.Network : DownloadFailure.Other, e); }
        catch (Exception e) { Fail(id, DownloadFailure.Other, e); }
        finally
        {
            lock (_gate) { _running.Remove(id); _segments.Remove(id); _ownLimits.Remove(id); }
            Pump(q.Id);
            CheckQueueFinished(q);
        }
    }
}
