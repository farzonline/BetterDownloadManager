namespace BDM.Core.Engine;

public enum PostAction { None, OpenFile, RunProgram, Sleep, Hibernate, Shutdown }

public record QueueDefinition(
    string Id,
    string Name,
    int MaxConcurrent,
    int Connections,
    TimeSpan? StartAt,
    TimeSpan? StopAt,
    PostAction OnFinish)
{
    public static QueueDefinition Main { get; } = new("main", "Main download queue", 3, 8, null, null, PostAction.None);
}
