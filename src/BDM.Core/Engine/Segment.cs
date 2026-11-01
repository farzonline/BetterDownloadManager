namespace BDM.Core.Engine;

/// <summary>A byte range [Start, End] (inclusive) of the file and how much of it is on disk.</summary>
public class Segment
{
    public long Start { get; init; }
    public long End { get; set; }
    public long Downloaded;
    public long Remaining => End - Start + 1 - Downloaded;
}
