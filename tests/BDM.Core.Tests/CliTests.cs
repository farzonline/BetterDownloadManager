using BDM.Cli;
using BDM.Core.Api;
using BDM.Core.Data;
using BDM.Core.Engine;
using BDM.Core.Http;

namespace BDM.Core.Tests;

public class CliTests : IDisposable
{
    const string Token = "cli-test-token-0123456789abcdef";
    readonly DownloadManager _manager;
    readonly RestApi _api;
    readonly string _dataDir = Directory.CreateTempSubdirectory().FullName;

    public CliTests()
    {
        var store = new DownloadStore(Path.Combine(Directory.CreateTempSubdirectory().FullName, "bdm.db"));
        store.SaveQueue(new QueueDefinition("main", "Main", 0, 2, null, null, PostAction.None));
        _manager = new DownloadManager(store, _ => new HttpClient());
        _api = new RestApi(_manager, uri => _manager.Add(new DownloadRequest(uri, null, null, new Dictionary<string, string>(), null, null, null, Path.GetTempPath())), Token, RestApi.FreePort());
        _api.Start();
        ApiInfo.Write(_dataDir, _api.Port, Token);
    }

    public void Dispose() => _api.Dispose();

    async Task<(int Code, string Out, string Err)> Run(params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        var code = await CliRunner.RunAsync(args, o, e, _dataDir);
        return (code, o.ToString(), e.ToString());
    }

    [Fact]
    public async Task Cli_Add_PostsToApi()
    {
        var (code, output, _) = await Run("add", "https://files.example/a/movie.mkv");
        Assert.Equal(0, code);
        var rec = Assert.Single(_manager.All());
        Assert.Equal("https://files.example/a/movie.mkv", rec.Request.Url.ToString());
        Assert.Contains(rec.Id.ToString(), output);
    }

    [Fact]
    public async Task Cli_List_ShowsStateAndName_AndJsonFlagPrintsRawJson()
    {
        _manager.Add(new DownloadRequest(new Uri("https://x.example/holiday.mp4"), null, null, new Dictionary<string, string>(), null, null, null, Path.GetTempPath()));
        var (code, output, _) = await Run("list");
        Assert.Equal(0, code);
        Assert.Contains("holiday.mp4", output);
        Assert.Contains("Queued", output);

        var (_, json, _) = await Run("list", "--json");
        Assert.StartsWith("[", json.TrimStart());
        Assert.Contains("\"name\":\"holiday.mp4\"", json);
    }

    [Fact]
    public async Task Cli_Pause_And_Resume_AcceptAnIdPrefix()
    {
        var id = _manager.Add(new DownloadRequest(new Uri("https://x.example/a.zip"), null, null, new Dictionary<string, string>(), null, null, null, Path.GetTempPath()));
        var prefix = id.ToString()[..8];
        Assert.Equal(0, (await Run("pause", prefix)).Code);
        Assert.Equal(DownloadState.Paused, _manager.Get(id)!.State);
        Assert.Equal(0, (await Run("resume", prefix)).Code);
        Assert.Equal(DownloadState.Queued, _manager.Get(id)!.State);
    }

    [Fact]
    public async Task Cli_Pause_UnknownOrAmbiguousId_ExplainsAndFails()
    {
        var (code, _, err) = await Run("pause", "ffffffff");
        Assert.Equal(1, code);
        Assert.Contains("No download matches", err);
        Assert.Empty(_manager.All());
    }

    [Fact]
    public async Task Cli_Status_PrintsOneDownload()
    {
        var id = _manager.Add(new DownloadRequest(new Uri("https://x.example/a.zip"), null, null, new Dictionary<string, string>(), null, null, null, Path.GetTempPath()));
        var (code, output, _) = await Run("status", id.ToString()[..8]);
        Assert.Equal(0, code);
        Assert.Contains("a.zip", output);
        Assert.Contains("https://x.example/a.zip", output);
    }

    [Fact]
    public async Task Cli_AppNotRunning_NoApiFile_Exit2()
    {
        var empty = Directory.CreateTempSubdirectory().FullName;
        var e = new StringWriter();
        var code = await CliRunner.RunAsync(["list"], new StringWriter(), e, empty);
        Assert.Equal(2, code);
        Assert.Contains("isn't running", e.ToString());
    }

    [Fact]
    public async Task Cli_StaleApiFile_NothingListening_Exit3()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        ApiInfo.Write(dir, RestApi.FreePort(), Token); // a port nobody listens on
        var e = new StringWriter();
        var code = await CliRunner.RunAsync(["list"], new StringWriter(), e, dir);
        Assert.Equal(3, code);
        Assert.Contains("isn't running", e.ToString());
    }

    [Fact]
    public async Task Cli_WrongToken_Exit4()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        ApiInfo.Write(dir, _api.Port, "some-other-token");
        var e = new StringWriter();
        var code = await CliRunner.RunAsync(["list"], new StringWriter(), e, dir);
        Assert.Equal(4, code);
        Assert.Contains("token", e.ToString());
    }

    [Theory]
    [InlineData]
    [InlineData("frobnicate")]
    [InlineData("add")]
    [InlineData("pause")]
    public async Task Cli_BadUsage_PrintsUsage_Exit1(params string[] args)
    {
        var (code, output, err) = await Run(args);
        Assert.Equal(1, code);
        Assert.Contains("bdm add <url>", output + err);
        Assert.Empty(_manager.All());
    }

    [Fact]
    public async Task Cli_Add_RejectsNonUrl_WithoutCallingTheApp()
    {
        var (code, _, err) = await Run("add", "not a url");
        Assert.Equal(1, code);
        Assert.Contains("address", err);
        Assert.Empty(_manager.All());
    }
}
