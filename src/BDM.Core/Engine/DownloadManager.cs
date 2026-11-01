using System.Security.Cryptography;
using BDM.Core.Data;
using BDM.Core.Files;
using BDM.Core.Http;

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

    public DownloadOptions BaseOptions { get; set; } = new();
    public event Action<DownloadRecord>? Changed;

    public DownloadManager(DownloadStore store, Func<string?, HttpClient> httpFor)
    {
        _store = store;
        _httpFor = httpFor;
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

    /// <summary>Starts queued downloads of a queue until its concurrency limit is reached.</summary>
    void Pump(string queueId)
    {
        var q = _store.Queue(queueId) ?? QueueDefinition.Main;
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
            Update(id, r => r with { State = DownloadState.Completed, Downloaded = result.Bytes, Total = result.Bytes, FilePath = result.Path, Error = null }, persist: true);
        }
        catch (OperationCanceledException)
        {
            // Pause() sets Paused before cancelling; Remove() deletes the row. Anything else (shutdown) parks it as Paused.
            Update(id, r => r.State == DownloadState.Running ? r with { State = DownloadState.Paused } : r, persist: true);
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
        }
    }
}
