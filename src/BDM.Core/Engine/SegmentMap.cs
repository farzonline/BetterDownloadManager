namespace BDM.Core.Engine;

public class SegmentMap
{
    readonly List<Segment> _segments;
    public long Length { get; }
    public IReadOnlyList<Segment> Segments => _segments;
    public bool IsComplete => Snapshot().All(s => s.Remaining <= 0);

    /// <summary>Thread-safe copy of the segment list (splits can happen while workers run).</summary>
    public IReadOnlyList<Segment> Snapshot() { lock (_segments) return _segments.ToList(); }

    public SegmentMap(long length, IEnumerable<Segment> segments)
    {
        Length = length;
        _segments = segments.OrderBy(s => s.Start).ToList();
    }

    public static SegmentMap Create(long length, int n)
    {
        n = (int)Math.Max(1, Math.Min(n, length));
        var size = length / n;
        var list = new List<Segment>(n);
        for (var i = 0; i < n; i++)
        {
            var start = i * size;
            list.Add(new Segment { Start = start, End = i == n - 1 ? length - 1 : start + size - 1 });
        }
        return new SegmentMap(length, list);
    }

    /// <summary>Splits the segment with the most bytes left in half; the new tail segment is returned.</summary>
    public bool TrySplitLargest(long minSize, out Segment created)
    {
        lock (_segments)
        {
            created = null!;
            var largest = _segments.MaxBy(s => s.Remaining);
            if (largest == null || largest.Remaining < 2 * minSize) return false;
            var mid = largest.Start + largest.Downloaded + largest.Remaining / 2;
            created = new Segment { Start = mid, End = largest.End };
            largest.End = mid - 1;
            _segments.Add(created);
            _segments.Sort((a, b) => a.Start.CompareTo(b.Start));
            return true;
        }
    }

    /// <summary>True when the segments tile [0, Length-1] exactly.</summary>
    internal bool IsContiguous()
    {
        long next = 0;
        foreach (var s in _segments) { if (s.Start != next || s.End < s.Start || s.Downloaded < 0 || s.Downloaded > s.End - s.Start + 1) return false; next = s.End + 1; }
        return next == Length;
    }
}
