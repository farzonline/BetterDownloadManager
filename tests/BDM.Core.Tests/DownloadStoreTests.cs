using BDM.Core.Data;
using BDM.Core.Engine;
using BDM.Core.Http;

namespace BDM.Core.Tests;

public class DownloadStoreTests
{
    static DownloadStore NewStore() => new(Path.Combine(Directory.CreateTempSubdirectory().FullName, "bdm.db"));

    internal static DownloadRecord Rec(string url, string? name = null, string queue = "main")
    {
        var req = new DownloadRequest(new Uri(url), name, "http://ref/", new Dictionary<string, string> { ["X-A"] = "1" },
            "a=b", null, "Video", Path.GetTempPath());
        return new DownloadRecord(Guid.NewGuid(), req, DownloadState.Queued, 0, null, null, queue, DateTime.UtcNow);
    }

    [Fact]
    public void Store_RoundTripsRecord()
    {
        using var store = NewStore();
        var r = Rec("http://x/a.zip", "a.zip") with { State = DownloadState.Failed, Downloaded = 42, Total = 100, Failure = DownloadFailure.AuthExpired, Error = "e" };
        store.Upsert(r);
        var g = store.Get(r.Id)!;
        Assert.Equal(r.Request.Url, g.Request.Url);
        Assert.Equal("1", g.Request.Headers["X-A"]);
        Assert.Equal("a=b", g.Request.CookieHeader);
        Assert.Equal(DownloadState.Failed, g.State);
        Assert.Equal(42, g.Downloaded);
        Assert.Equal(100, g.Total);
        Assert.Equal(DownloadFailure.AuthExpired, g.Failure);
        Assert.Null(store.Get(Guid.NewGuid()));
    }

    [Fact]
    public void Store_Search_MatchesNameAndUrl()
    {
        using var store = NewStore();
        store.Upsert(Rec("http://host/one.zip", "holiday.zip"));
        store.Upsert(Rec("http://cdn.example/two.bin"));
        Assert.Single(store.Search("holiday"));
        Assert.Single(store.Search("cdn.example"));
        Assert.Equal(2, store.Search("").Count);
        Assert.Empty(store.Search("nothing-matches"));
    }

    [Fact]
    public void Store_ExportImport_RestoresQueuesAndDownloads()
    {
        using var a = NewStore();
        a.SaveQueue(new QueueDefinition("night", "Night", 1, 4, TimeSpan.FromHours(23), TimeSpan.FromHours(6), PostAction.Shutdown));
        var r = Rec("http://x/a.zip", "a.zip", "night");
        a.Upsert(r);
        var json = a.Export();

        using var b = NewStore();
        b.Import(json);
        Assert.Equal(PostAction.Shutdown, b.Queues().Single(q => q.Id == "night").OnFinish);
        Assert.Equal(TimeSpan.FromHours(23), b.Queues().Single(q => q.Id == "night").StartAt);
        Assert.NotNull(b.Get(r.Id));
        Assert.Contains(b.Queues(), q => q.Id == "main");
    }
}
