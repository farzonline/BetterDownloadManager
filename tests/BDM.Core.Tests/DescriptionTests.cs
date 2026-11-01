using BDM.Core.Data;
using BDM.Core.Engine;
using BDM.Core.Http;

namespace BDM.Core.Tests;

public class DescriptionTests
{
    [Fact]
    public void Description_IsStoredWithTheDownload()
    {
        var db = Path.Combine(Directory.CreateTempSubdirectory().FullName, "bdm.db");
        var req = new DownloadRequest(new Uri("https://x.example/a.zip"), null, null, new Dictionary<string, string>(), null, null, null, ".")
            { Description = "Season 2 backup" };
        var id = Guid.NewGuid();
        using (var store = new DownloadStore(db))
            store.Upsert(new DownloadRecord(id, req, DownloadState.Paused, 0, null, null, "main", DateTime.UtcNow));
        using var again = new DownloadStore(db);
        Assert.Equal("Season 2 backup", again.Get(id)!.Request.Description);
    }
}
