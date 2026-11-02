using BDM.Core.Engine;

namespace BDM.App.ViewModels;

public sealed record SegmentRow(int Number, string Downloaded, string Rate, string Status);

/// <summary>
/// IDM's per-connection table in the progress window: each part's bytes, its own speed (measured over a second, so it
/// doesn't flicker) and what it is doing. Parts are told apart by where they start; a split adds a new one.
/// </summary>
public sealed class SegmentRows
{
    readonly Dictionary<long, (long Bytes, DateTimeOffset At, double Rate)> _seen = new();

    public IReadOnlyList<SegmentRow> Update(IReadOnlyList<Segment> segments, bool running, DateTimeOffset now)
    {
        var rows = new List<SegmentRow>(segments.Count);
        foreach (var s in segments)
        {
            var got = Interlocked.Read(ref s.Downloaded);
            var len = s.End - s.Start + 1;
            var rate = 0.0;
            if (_seen.TryGetValue(s.Start, out var last))
            {
                rate = last.Rate;
                var secs = (now - last.At).TotalSeconds;
                if (secs >= 1) _seen[s.Start] = (got, now, rate = Math.Max(0, got - last.Bytes) / secs);
            }
            else _seen[s.Start] = (got, now, 0);

            var done = got >= len;
            if (done || !running) rate = 0;
            var status = done ? "Finished" : !running ? "Paused" : rate > 0 ? "Receiving data" : got == 0 ? "Connecting…" : "Waiting for data";
            rows.Add(new SegmentRow(rows.Count + 1, $"{Format.Size(got)} of {Format.Size(len)}", Format.Speed(rate), status));
        }
        return rows;
    }
}
