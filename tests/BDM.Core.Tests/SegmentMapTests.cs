using BDM.Core.Engine;

namespace BDM.Core.Tests;

public class SegmentMapTests
{
    [Theory]
    [InlineData(1L)]
    [InlineData(7L)]
    [InlineData(10_000_000_001L)]
    public void Create_CoversWholeFileWithoutGaps(long length)
    {
        var map = SegmentMap.Create(length, 8);
        long next = 0;
        foreach (var s in map.Segments) { Assert.Equal(next, s.Start); Assert.True(s.End >= s.Start); next = s.End + 1; }
        Assert.Equal(length, next);
    }

    [Fact]
    public void Split_HalvesLargestRemaining()
    {
        var map = SegmentMap.Create(1000, 2); // 0-499, 500-999
        map.Segments[0].Downloaded = 400;     // 100 left; segment 1 has 500 left
        Assert.True(map.TrySplitLargest(10, out var created));
        Assert.Equal(750, created.Start);
        Assert.Equal(999, created.End);
        Assert.Equal(749, map.Segments[1].End);
        Assert.Equal(3, map.Segments.Count);
    }

    [Fact]
    public void Split_RefusesBelowMinSize()
    {
        var map = SegmentMap.Create(100, 2);
        Assert.False(map.TrySplitLargest(50, out _));
    }

    [Fact]
    public void Sidecar_RoundTripsProgressAndValidators()
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory().FullName, "a.bdmpart.json");
        var map = SegmentMap.Create(1000, 4);
        map.Segments[2].Downloaded = 77;
        SegmentSidecar.Save(path, map, "\"e1\"", "Mon, 01 Jan 2024 00:00:00 GMT");
        var loaded = SegmentSidecar.Load(path);
        Assert.NotNull(loaded);
        Assert.Equal("\"e1\"", loaded!.Value.ETag);
        Assert.Equal("Mon, 01 Jan 2024 00:00:00 GMT", loaded.Value.LastModified);
        Assert.Equal(1000, loaded.Value.Map.Length);
        Assert.Equal(77, loaded.Value.Map.Segments[2].Downloaded);
    }

    [Fact]
    public void Sidecar_HeldByAnotherProgram_IsSkippedNotThrown()
    {
        // An antivirus or indexer holding the resume file used to fail the download with "Access to the path is denied".
        var path = Path.Combine(Directory.CreateTempSubdirectory().FullName, "held.bdmpart.json");
        var map = SegmentMap.Create(1000, 4);
        SegmentSidecar.Save(path, map, null, null);
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.False(SegmentSidecar.Save(path, map, null, null));
        Assert.True(SegmentSidecar.Save(path, map, null, null));
    }

    [Fact]
    public void Sidecar_CorruptFile_ReturnsNull()
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory().FullName, "bad.json");
        File.WriteAllText(path, "{not json");
        Assert.Null(SegmentSidecar.Load(path));
        Assert.Null(SegmentSidecar.Load(path + ".missing"));
    }
}
