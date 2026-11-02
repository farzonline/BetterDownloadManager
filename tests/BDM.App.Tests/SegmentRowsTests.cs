using BDM.App.ViewModels;
using BDM.Core.Engine;

namespace BDM.App.Tests;

/// <summary>IDM's per-connection table in the progress window: each part's bytes, speed and state.</summary>
public class SegmentRowsTests
{
    static readonly DateTimeOffset T0 = DateTimeOffset.UnixEpoch;

    [Fact]
    public void EachPart_ShowsItsBytes_ItsOwnSpeed_AndWhatItIsDoing()
    {
        var a = new Segment { Start = 0, End = 999_999 };
        var b = new Segment { Start = 1_000_000, End = 1_999_999 };
        var c = new Segment { Start = 2_000_000, End = 2_099_999, Downloaded = 100_000 };
        var rows = new SegmentRows();
        rows.Update([a, b, c], running: true, T0);

        a.Downloaded = 500_000; // 500 KB in one second
        var r = rows.Update([a, b, c], running: true, T0.AddSeconds(1));

        Assert.Equal([1, 2, 3], r.Select(x => x.Number));
        Assert.Equal("488.3 KB of 976.6 KB", r[0].Downloaded);
        Assert.Equal("488.3 KB/s", r[0].Rate);
        Assert.Equal("Receiving data", r[0].Status);
        Assert.Equal("Connecting…", r[1].Status); // nothing yet
        Assert.Equal("", r[1].Rate);
        Assert.Equal("Finished", r[2].Status);
    }

    [Fact]
    public void AStoppedDownload_ShowsItsPartsAsPaused()
    {
        var a = new Segment { Start = 0, End = 99, Downloaded = 10 };
        var r = new SegmentRows().Update([a], running: false, T0);
        Assert.Equal("Paused", r[0].Status);
    }

    [Fact]
    public void APartThatStopsReceiving_SaysItIsWaiting()
    {
        var a = new Segment { Start = 0, End = 999, Downloaded = 10 };
        var rows = new SegmentRows();
        rows.Update([a], running: true, T0);
        var r = rows.Update([a], running: true, T0.AddSeconds(1));
        Assert.Equal("Waiting for data", r[0].Status);
    }
}
