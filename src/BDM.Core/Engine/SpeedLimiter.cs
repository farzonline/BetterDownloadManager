using System.Diagnostics;

namespace BDM.Core.Engine;

/// <summary>Shared rate limiter: each grant reserves its slot on a timeline, so there is no burst.</summary>
public class SpeedLimiter
{
    readonly object _gate = new();
    readonly Stopwatch _clock = Stopwatch.StartNew();
    double _next; // seconds on _clock when the next grant may start
    long _bytesPerSec;

    public SpeedLimiter(long bytesPerSec) => _bytesPerSec = bytesPerSec;

    /// <summary>0 or less means unlimited.</summary>
    public void SetLimit(long bytesPerSec) { lock (_gate) _bytesPerSec = bytesPerSec; }

    public async ValueTask AcquireAsync(int bytes, CancellationToken ct)
    {
        double wait;
        lock (_gate)
        {
            if (_bytesPerSec <= 0) return;
            var now = _clock.Elapsed.TotalSeconds;
            var start = Math.Max(now, _next);
            _next = start + (double)bytes / _bytesPerSec;
            wait = start - now;
        }
        if (wait > 0) await Task.Delay(TimeSpan.FromSeconds(wait), ct);
        else ct.ThrowIfCancellationRequested();
    }
}
