namespace BDM.Core.Engine;

public static class Scheduler
{
    /// <summary>True when a queue may run at <paramref name="now"/>. A missing start/stop means "from midnight"/"until midnight".</summary>
    public static bool IsWindowOpen(QueueDefinition q, DateTime now)
    {
        if (q.StartAt is null && q.StopAt is null) return true;
        var start = q.StartAt ?? TimeSpan.Zero;
        var stop = q.StopAt ?? TimeSpan.FromHours(24);
        var t = now.TimeOfDay;
        if (start == stop) return true;
        return start < stop ? t >= start && t < stop : t >= start || t < stop; // overnight window wraps midnight
    }
}
