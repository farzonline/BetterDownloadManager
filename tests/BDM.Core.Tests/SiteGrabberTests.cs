using BDM.Core.Grabber;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace BDM.Core.Tests;

public class SiteGrabberTests
{
    sealed class Site : IAsyncDisposable
    {
        readonly WebApplication _app;
        public Uri Base { get; private set; } = null!;
        public Site(Dictionary<string, string> pages)
        {
            var b = WebApplication.CreateBuilder();
            b.Logging.ClearProviders();
            b.WebHost.UseUrls("http://127.0.0.1:0");
            _app = b.Build();
            _app.MapFallback(async ctx =>
            {
                if (pages.TryGetValue(ctx.Request.Path.Value!, out var html)) { ctx.Response.ContentType = "text/html"; await ctx.Response.WriteAsync(html); }
                else { ctx.Response.ContentType = "application/octet-stream"; await ctx.Response.WriteAsync("bin"); }
            });
            _app.StartAsync().GetAwaiter().GetResult();
            Base = new Uri(_app.Urls.First());
        }
        public ValueTask DisposeAsync() => _app.DisposeAsync();
    }

    static string[] Names(IEnumerable<Uri> uris) => uris.Select(u => u.AbsolutePath).OrderBy(x => x).ToArray();

    static readonly Dictionary<string, string> Pages = new()
    {
        ["/"] = "<a href='/a.zip'>a</a><img src=\"b.jpg\"><a href='/page2'>p2</a><a href='http://other.invalid/x.zip'>x</a>",
        ["/page2"] = "<a href='c.zip'>c</a><a href='/page3'>p3</a>",
        ["/page3"] = "<a href='/d.zip'>d</a>",
    };

    [Fact]
    public async Task Crawl_DepthOne_FindsLinkedFilesFilteredByExt()
    {
        await using var site = new Site(Pages);
        var opts = new GrabOptions(1, new[] { "zip" }, Array.Empty<string>(), true);
        var found = await SiteGrabber.CrawlAsync(site.Base, opts, new HttpClient(), default);
        Assert.Equal(new[] { "/a.zip", "/c.zip" }, Names(found)); // b.jpg filtered out, d.zip is two levels deep
    }

    [Fact]
    public async Task Crawl_DepthZero_OnlyStartPage()
    {
        await using var site = new Site(Pages);
        var found = await SiteGrabber.CrawlAsync(site.Base, new GrabOptions(0, new[] { "zip", "jpg" }, Array.Empty<string>(), true), new HttpClient(), default);
        Assert.Equal(new[] { "/a.zip", "/b.jpg" }, Names(found));
    }

    [Fact]
    public async Task Crawl_SameHostOnly_SkipsExternal()
    {
        await using var site = new Site(Pages);
        var same = await SiteGrabber.CrawlAsync(site.Base, new GrabOptions(0, new[] { "zip" }, Array.Empty<string>(), true), new HttpClient(), default);
        var any = await SiteGrabber.CrawlAsync(site.Base, new GrabOptions(0, new[] { "zip" }, Array.Empty<string>(), false), new HttpClient(), default);
        Assert.DoesNotContain(same, u => u.Host == "other.invalid");
        Assert.Contains(any, u => u.Host == "other.invalid");
    }

    [Fact]
    public async Task Crawl_ExcludeExt_Removes()
    {
        await using var site = new Site(Pages);
        var found = await SiteGrabber.CrawlAsync(site.Base, new GrabOptions(0, Array.Empty<string>(), new[] { "jpg" }, true), new HttpClient(), default);
        Assert.Equal(new[] { "/a.zip" }, Names(found));
    }

    [Fact]
    public async Task Crawl_DoesNotLoop_OnCircularLinks()
    {
        await using var site = new Site(new() { ["/"] = "<a href='/p'>p</a><a href='f.zip'>f</a>", ["/p"] = "<a href='/'>home</a>" });
        var task = SiteGrabber.CrawlAsync(site.Base, new GrabOptions(10, new[] { "zip" }, Array.Empty<string>(), true), new HttpClient(), default);
        var done = await Task.WhenAny(task, Task.Delay(5000));
        Assert.Same(task, done);
        Assert.Equal(new[] { "/f.zip" }, Names(await task));
    }
}
