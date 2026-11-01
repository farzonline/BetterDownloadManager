using System.Security.Cryptography;
using BDM.Core.Data;
using BDM.Core.Files;
using BDM.Core.Http;
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

    readonly HashSet<Guid> _windowStopped = new();
    readonly HashSet<string> _batchDone = new();
    readonly Dictionary<string, string> _lastCompleted = new();
    readonly Timer _tick;

    public DownloadOptions BaseOptions { get; set; } = new();
    /// <summary>Needed for stream (HLS/DASH) jobs; set by the app once ffmpeg handling is configured.</summary>
    public StreamDownloader? Streams { get; set; }
    public StreamOptions StreamOptions { get; set; } = new();
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

    public IReadOnlyList<DownloadRecord> All() { lock (_gate) return _records.Values.OrderBy(r => r.Added).ToList(); }

    public Guid Add(DownloadRequest request, string queueId = "main", string? expectedSha256 = null)
    {
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

    /// <summary>JSON dump of every download and queue (settings export / backup).</summary>
    public string ExportAll() => _store.Export();

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
        if (deleteFile && r.FilePath != null)
            foreach (var p in new[] { r.FilePath, r.FilePath + ".bdmpart", r.FilePath + ".bdmpart.json" })
                try { File.Delete(p); } catch (IOException) { }
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
        var toStart = new List<DownloadRecord>();
        lock (_gate)
        {
            var running = _records.Values.Count(r => r.QueueId == queueId && r.State == DownloadState.Running);
            foreach (var next in _records.Values.Where(r => r.QueueId == queueId && r.State == DownloadState.Queued).OrderBy(r => r.Added))
            {
                if (running >= q.MaxConcurrent) break;
                var started = next with { State = DownloadState.Running };
                _records[next.Id] = started;
                toStart.Add(started);
                running++;
            }
        }
        foreach (var r in toStart)
        {
            _store.Upsert(r);
            Changed?.Invoke(r);
            _ = Task.Run(() => RunAsync(r.Id, q));
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

    sealed class SyncProgress(Action<DownloadProgress> on) : IProgress<DownloadProgress>
    {
        public void Report(DownloadProgress v) => on(v);
    }

    async Task RunAsync(Guid id, QueueDefinition q)
    {
        using var cts = new CancellationTokenSource();
        lock (_gate) _running[id] = cts;
        var ct = cts.Token;
        try
        {
            var rec = Get(id)!;
            if (rec.Stream != null)
            {
                var streams = Streams ?? throw new InvalidOperationException("Stream downloads are not configured.");
                var path = await streams.DownloadAsync(rec.Stream, rec.StreamChoice!, rec.Request.DestinationDir,
                    new SyncProgress(p => Update(id, r => r with { Downloaded = p.Downloaded }, persist: false)), ct, StreamOptions);
                var length = new FileInfo(path).Length;
                // Mark the batch BEFORE the state flips: a sibling's finish-check must never see this download as
                // done without also seeing the flag, or the post-action could fire twice.
                lock (_gate) { _batchDone.Add(q.Id); _lastCompleted[q.Id] = path; }
                Update(id, r => r with { State = DownloadState.Completed, Downloaded = length, Total = length, FilePath = path, Error = null }, persist: true);
                return;
            }
            var http = _httpFor(rec.Request.ProxyProfileId);
            var probe = await Prober.ProbeAsync(http, rec.Request, ct);
            Update(id, r => r with { Total = probe.Length, FilePath = Path.Combine(r.Request.DestinationDir, probe.FileName) }, persist: true);

            var opts = BaseOptions with { Connections = q.Connections };
            var progress = new SyncProgress(p => Update(id, r => r with { Downloaded = p.Downloaded, Total = p.Total ?? r.Total }, persist: false));
            var result = await new SegmentedDownloader(http).RunAsync(rec.Request, probe, opts, progress, ct);

            if (rec.ExpectedSha256 is { } want &&
                !string.Equals(await Checksum.ComputeAsync(result.Path, HashAlgorithmName.SHA256), want, StringComparison.OrdinalIgnoreCase))
            {
                Update(id, r => r with { State = DownloadState.Failed, Failure = DownloadFailure.ServerChanged, Error = "Checksum mismatch: the file does not match the expected SHA-256.", FilePath = result.Path }, persist: true);
                return;
            }
            lock (_gate) { _batchDone.Add(q.Id); _lastCompleted[q.Id] = result.Path; } // before the state flips, see above
            Update(id, r => r with { State = DownloadState.Completed, Downloaded = result.Bytes, Total = result.Bytes, FilePath = result.Path, Error = null }, persist: true);
        }
        catch (OperationCanceledException)
        {
            // Pause() sets Paused before cancelling; Remove() deletes the row; a closing schedule window re-queues.
            // Anything else (shutdown) parks it as Paused.
            bool windowStop;
            lock (_gate) windowStop = _windowStopped.Remove(id);
            Update(id, r => r.State == DownloadState.Running ? r with { State = windowStop ? DownloadState.Queued : DownloadState.Paused } : r, persist: true);
        }
        catch (ProtectedContentException e)
        {
            Update(id, r => r with { State = DownloadState.Failed, Failure = DownloadFailure.Protected, Error = e.Message }, persist: true);
        }
        catch (DownloadException e)
        {
            Update(id, r => r with { State = DownloadState.Failed, Failure = e.Kind, Error = e.Message }, persist: true);
        }
        catch (Exception e)
        {
            Update(id, r => r with { State = DownloadState.Failed, Failure = DownloadFailure.Network, Error = e.Message }, persist: true);
        }
        finally
        {
            lock (_gate) _running.Remove(id);
            Pump(q.Id);
            CheckQueueFinished(q);
        }
    }
}
