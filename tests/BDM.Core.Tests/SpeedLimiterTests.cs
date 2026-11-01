using System.Diagnostics;
using BDM.Core.Engine;

namespace BDM.Core.Tests;

public class SpeedLimiterTests
{
    [Fact]
    public async Task Acquire_ThrottlesToLimit()
    {
        var limiter = new SpeedLimiter(250_000);
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 50; i++) await limiter.AcquireAsync(10_000, default); // 500 KB
        sw.Stop();
        Assert.InRange(sw.Elapsed.TotalSeconds, 1.8, 3.0);
    }

    [Fact]
    public async Task Acquire_Cancels()
    {
        var limiter = new SpeedLimiter(1);
        await limiter.AcquireAsync(1, default);
        using var cts = new CancellationTokenSource(100);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await limiter.AcquireAsync(100, cts.Token));
    }

    [Fact]
    public async Task SetLimit_ZeroMeansUnlimited()
    {
        var limiter = new SpeedLimiter(1);
        limiter.SetLimit(0);
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 100; i++) await limiter.AcquireAsync(1_000_000, default);
        Assert.True(sw.Elapsed.TotalSeconds < 1);
    }
}
