using BDM.Core.Batch;

namespace BDM.Core.Tests;

public class BatchPatternTests
{
    [Fact]
    public void Expand_ZeroPaddedRange()
    {
        var r = BatchPattern.Expand("http://x/f[001-003].jpg");
        Assert.Equal(new[] { "http://x/f001.jpg", "http://x/f002.jpg", "http://x/f003.jpg" }, r.Select(u => u.ToString()));
    }

    [Fact]
    public void Expand_UnpaddedRange_NoPadding() =>
        Assert.Equal(new[] { "http://x/9", "http://x/10" }, BatchPattern.Expand("http://x/[9-10]").Select(u => u.ToString()));

    [Fact]
    public void Expand_TwoGroupsMultiply_LeftmostSlowest()
    {
        var r = BatchPattern.Expand("http://x/[a-b]/y[1-2]").Select(u => u.ToString()).ToList();
        Assert.Equal(new[] { "http://x/a/y1", "http://x/a/y2", "http://x/b/y1", "http://x/b/y2" }, r);
    }

    [Fact]
    public void Expand_NoBrackets_ReturnsItself() =>
        Assert.Equal("http://x/file.zip", Assert.Single(BatchPattern.Expand("http://x/file.zip")).ToString());

    [Fact]
    public void Expand_Ipv6HostBrackets_AreNotAPattern() =>
        Assert.Single(BatchPattern.Expand("http://[::1]:8080/a.zip"));

    [Fact]
    public void Expand_AbsurdlyLarge_Throws() =>
        Assert.Throws<ArgumentException>(() => BatchPattern.Expand("http://x/[0-99999]/[0-99999]"));
}
