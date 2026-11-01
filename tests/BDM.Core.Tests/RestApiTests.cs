using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using BDM.Core.Api;
using BDM.Core.Data;
using BDM.Core.Engine;
using BDM.Core.Http;

namespace BDM.Core.Tests;

public class RestApiTests : IDisposable
{
    const string Token = "test-token-0123456789abcdef";
    readonly DownloadManager _manager;
    readonly List<Uri> _added = new();
    readonly RestApi _api;
    readonly HttpClient _http = new();

    public RestApiTests()
    {
        var store = new DownloadStore(Path.Combine(Directory.CreateTempSubdirectory().FullName, "bdm.db"));
        store.SaveQueue(new QueueDefinition("main", "Main", 0, 2, null, null, PostAction.None)); // nothing starts
        _manager = new DownloadManager(store, _ => new HttpClient());
        _api = new RestApi(_manager, uri =>
        {
            _added.Add(uri);
            return _manager.Add(new DownloadRequest(uri, null, null, new Dictionary<string, string>(), null, null, null, Path.GetTempPath()));
        }, Token, RestApi.FreePort());
        _api.Start();
    }

    public void Dispose() { _api.Dispose(); _http.Dispose(); }

    HttpRequestMessage Req(HttpMethod m, string path, string? token = Token, object? body = null)
    {
        var r = new HttpRequestMessage(m, new Uri($"http://127.0.0.1:{_api.Port}{path}"));
        if (token != null) r.Headers.Add("X-BDM-Token", token);
        if (body != null) r.Content = JsonContent.Create(body);
        return r;
    }

    [Fact]
    public async Task NoTokenOrWrongToken_Is401()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await _http.SendAsync(Req(HttpMethod.Get, "/downloads", token: null))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _http.SendAsync(Req(HttpMethod.Get, "/downloads", token: "wrong"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _http.SendAsync(Req(HttpMethod.Post, "/add", token: null, body: new { url = "https://x/a.zip" }))).StatusCode);
        Assert.Empty(_added);
    }

    [Fact]
    public void BindsLoopbackOnly()
    {
        Assert.Equal("127.0.0.1", RestApi.ListenAddress);
        Assert.Equal($"http://127.0.0.1:{_api.Port}/", _api.Prefix);
    }

    [Fact]
    public async Task ForeignHostHeader_IsRejected_ProtectingAgainstDnsRebinding()
    {
        var r = Req(HttpMethod.Get, "/downloads");
        r.Headers.Host = "evil.example";
        Assert.Equal((HttpStatusCode)421, (await _http.SendAsync(r)).StatusCode);

        var ok = Req(HttpMethod.Get, "/downloads");
        ok.Headers.Host = $"localhost:{_api.Port}";
        Assert.Equal(HttpStatusCode.OK, (await _http.SendAsync(ok)).StatusCode);
    }

    [Fact]
    public async Task BrowserOriginHeader_IsRejected_ProtectingAgainstWebPages()
    {
        var r = Req(HttpMethod.Post, "/add", body: new { url = "https://x/a.zip" });
        r.Headers.Add("Origin", "https://evil.example");
        Assert.Equal(HttpStatusCode.Forbidden, (await _http.SendAsync(r)).StatusCode);
        Assert.Empty(_added);
    }

    [Fact]
    public async Task Add_ReturnsId_AndQueuesTheDownload()
    {
        var resp = await _http.SendAsync(Req(HttpMethod.Post, "/add", body: new { url = "https://files.example/a/movie.mkv" }));
        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
        var id = Guid.Parse((await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!);
        Assert.Equal(new Uri("https://files.example/a/movie.mkv"), Assert.Single(_added));
        Assert.NotNull(_manager.Get(id));
    }

    [Theory]
    [InlineData("""{"url":"not a url"}""")]
    [InlineData("""{"url":"file:///c:/windows/notepad.exe"}""")]
    [InlineData("""{"nothing":1}""")]
    [InlineData("""not json""")]
    public async Task Add_BadInput_Is400(string body)
    {
        var r = Req(HttpMethod.Post, "/add");
        r.Content = new StringContent(body, Encoding.UTF8, "application/json");
        Assert.Equal(HttpStatusCode.BadRequest, (await _http.SendAsync(r)).StatusCode);
        Assert.Empty(_added);
    }

    [Fact]
    public async Task ListAndGet_ShowDownloads()
    {
        var id = _manager.Add(new DownloadRequest(new Uri("https://x.example/holiday.mp4"), null, null, new Dictionary<string, string>(), null, null, null, Path.GetTempPath()));

        var list = await (await _http.SendAsync(Req(HttpMethod.Get, "/downloads"))).Content.ReadFromJsonAsync<JsonElement>();
        var item = Assert.Single(list.EnumerateArray());
        Assert.Equal(id.ToString(), item.GetProperty("id").GetString());
        Assert.Equal("holiday.mp4", item.GetProperty("name").GetString());
        Assert.Equal("Queued", item.GetProperty("state").GetString());

        var one = await (await _http.SendAsync(Req(HttpMethod.Get, $"/downloads/{id}"))).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("https://x.example/holiday.mp4", one.GetProperty("url").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await _http.SendAsync(Req(HttpMethod.Get, $"/downloads/{Guid.NewGuid()}"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _http.SendAsync(Req(HttpMethod.Get, "/downloads/not-a-guid"))).StatusCode);
    }

    [Fact]
    public async Task PauseAndResume_ChangeTheState()
    {
        var id = _manager.Add(new DownloadRequest(new Uri("https://x.example/a.zip"), null, null, new Dictionary<string, string>(), null, null, null, Path.GetTempPath()));
        Assert.Equal(HttpStatusCode.NoContent, (await _http.SendAsync(Req(HttpMethod.Post, $"/downloads/{id}/pause"))).StatusCode);
        Assert.Equal(DownloadState.Paused, _manager.Get(id)!.State);
        Assert.Equal(HttpStatusCode.NoContent, (await _http.SendAsync(Req(HttpMethod.Post, $"/downloads/{id}/resume"))).StatusCode);
        Assert.Equal(DownloadState.Queued, _manager.Get(id)!.State);
        Assert.Equal(HttpStatusCode.NotFound, (await _http.SendAsync(Req(HttpMethod.Post, $"/downloads/{Guid.NewGuid()}/pause"))).StatusCode);
    }

    [Fact]
    public async Task PauseOrResume_ThatCannotApply_Is409WithAReason()
    {
        var id = _manager.Add(new DownloadRequest(new Uri("https://x.example/a.zip"), null, null, new Dictionary<string, string>(), null, null, null, Path.GetTempPath()));
        // Queued: resume makes no sense
        var resume = await _http.SendAsync(Req(HttpMethod.Post, $"/downloads/{id}/resume"));
        Assert.Equal(HttpStatusCode.Conflict, resume.StatusCode);
        Assert.Contains("Queued", (await resume.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
        // Paused: pausing again makes no sense
        _manager.Pause(id);
        Assert.Equal(HttpStatusCode.Conflict, (await _http.SendAsync(Req(HttpMethod.Post, $"/downloads/{id}/pause"))).StatusCode);
        Assert.Equal(DownloadState.Paused, _manager.Get(id)!.State);
    }

    [Fact]
    public async Task UnknownRoute_Is404_AndWrongMethodIs405()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await _http.SendAsync(Req(HttpMethod.Get, "/nope"))).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await _http.SendAsync(Req(HttpMethod.Get, "/add"))).StatusCode);
    }

    [Fact]
    public async Task HugeBody_IsRefused()
    {
        var r = Req(HttpMethod.Post, "/add");
        r.Content = new StringContent("{\"url\":\"https://x/" + new string('a', 200_000) + "\"}", Encoding.UTF8, "application/json");
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await _http.SendAsync(r)).StatusCode);
    }

    [Fact]
    public void ApiInfo_RoundTripsThroughTheDataFolder_AndNewTokensAreLongAndUnique()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        ApiInfo.Write(dir, 4321, "abc");
        var info = ApiInfo.Read(dir)!;
        Assert.Equal((4321, "abc"), (info.Port, info.Token));
        Assert.Null(ApiInfo.Read(Path.Combine(dir, "missing")));
        File.WriteAllText(Path.Combine(dir, "api.json"), "{{{");
        Assert.Null(ApiInfo.Read(dir));
        var (a, b) = (RestApi.NewToken(), RestApi.NewToken());
        Assert.NotEqual(a, b);
        Assert.True(a.Length >= 32);
    }
}
