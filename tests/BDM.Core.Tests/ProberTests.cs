using BDM.Core.Http;

namespace BDM.Core.Tests;

public class ProberTests
{
    static DownloadRequest Req(Uri u) =>
        new(u, null, null, new Dictionary<string, string>(), null, null, null, Path.GetTempPath());

    [Fact]
    public async Task Probe_ReadsLengthAndRanges()
    {
        await using var s = TestServer.Start(5000);
        s.ETag = "\"abc\"";
        var p = await Prober.ProbeAsync(new HttpClient(), Req(s.FileUrl), default);
        Assert.Equal(5000, p.Length);
        Assert.True(p.AcceptsRanges);
        Assert.Equal("\"abc\"", p.ETag);
    }

    [Fact]
    public async Task Probe_FallsBackToRangeGetWhenHeadRejected()
    {
        await using var s = TestServer.Start(5000);
        s.RejectHead = true;
        var p = await Prober.ProbeAsync(new HttpClient(), Req(s.FileUrl), default);
        Assert.Equal(5000, p.Length);
        Assert.True(p.AcceptsRanges);
    }

    [Fact]
    public async Task Probe_NoRangeSupport_AcceptsRangesFalse()
    {
        await using var s = TestServer.Start(5000);
        s.SupportRanges = false;
        var p = await Prober.ProbeAsync(new HttpClient(), Req(s.FileUrl), default);
        Assert.Equal(5000, p.Length);
        Assert.False(p.AcceptsRanges);
    }
}
