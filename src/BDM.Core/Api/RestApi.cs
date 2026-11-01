using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BDM.Core.Data;
using BDM.Core.Engine;

namespace BDM.Core.Api;

/// <summary>Where the running app tells local tools how to reach its API (kept in the user's own data folder).</summary>
public record ApiInfo(int Port, string Token)
{
    const string FileName = "api.json";

    public static void Write(string dataDir, int port, string token)
    {
        Directory.CreateDirectory(dataDir);
        File.WriteAllText(Path.Combine(dataDir, FileName), JsonSerializer.Serialize(new ApiInfo(port, token)));
    }

    public static ApiInfo? Read(string dataDir)
    {
        try
        {
            var path = Path.Combine(dataDir, FileName);
            return File.Exists(path) ? JsonSerializer.Deserialize<ApiInfo>(File.ReadAllText(path)) : null;
        }
        catch (Exception e) when (e is JsonException or IOException) { return null; }
    }
}

/// <summary>
/// Local control API for scripts and the `bdm` tool. Bound to 127.0.0.1 only and guarded three ways: a secret token
/// header, a Host check (DNS rebinding) and a ban on browser requests (any Origin header). It does nothing a user
/// could not do in the app.
/// </summary>
public sealed class RestApi : IDisposable
{
    public const string ListenAddress = "127.0.0.1";
    const int MaxBody = 64 * 1024;
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    readonly DownloadManager _manager;
    readonly Func<Uri, Guid> _add;
    readonly byte[] _token;
    readonly HttpListener _listener = new();

    public int Port { get; }
    public string Prefix => $"http://{ListenAddress}:{Port}/";

    public RestApi(DownloadManager manager, Func<Uri, Guid> add, string token, int port)
    {
        _manager = manager;
        _add = add;
        _token = Encoding.UTF8.GetBytes(token);
        Port = port;
        _listener.Prefixes.Add(Prefix);
    }

    public static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    public static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    public void Start()
    {
        _listener.Start();
        _ = Task.Run(async () =>
        {
            while (_listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync(); }
                catch (Exception e) when (e is HttpListenerException or ObjectDisposedException or InvalidOperationException) { return; }
                _ = Task.Run(() => Handle(ctx));
            }
        });
    }

    static void Reply(HttpListenerContext ctx, int status, object? body = null)
    {
        ctx.Response.StatusCode = status;
        if (body != null)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(body, Json);
            ctx.Response.ContentType = "application/json";
            ctx.Response.ContentLength64 = bytes.Length;
            ctx.Response.OutputStream.Write(bytes);
        }
        ctx.Response.Close();
    }

    async Task Handle(HttpListenerContext ctx)
    {
        try
        {
            var req = ctx.Request;
            var host = req.Headers["Host"];
            if (host != $"{ListenAddress}:{Port}" && host != $"localhost:{Port}") { Reply(ctx, 421, new { error = "Unexpected Host header." }); return; }
            if (req.Headers["Origin"] != null) { Reply(ctx, 403, new { error = "Browser requests are not allowed." }); return; }
            var given = Encoding.UTF8.GetBytes(req.Headers["X-BDM-Token"] ?? "");
            if (!CryptographicOperations.FixedTimeEquals(given, _token)) { Reply(ctx, 401, new { error = "Missing or wrong X-BDM-Token." }); return; }

            var path = req.Url!.AbsolutePath.TrimEnd('/');
            var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var method = req.HttpMethod;

            if (parts is ["add"])
            {
                if (method != "POST") { Reply(ctx, 405); return; }
                await Add(ctx);
            }
            else if (parts is ["downloads"])
            {
                if (method != "GET") { Reply(ctx, 405); return; }
                Reply(ctx, 200, _manager.All().Select(Row).ToList());
            }
            else if (parts is ["downloads", var id] && Guid.TryParse(id, out var gid))
            {
                if (method != "GET") { Reply(ctx, 405); return; }
                if (_manager.Get(gid) is { } rec) Reply(ctx, 200, Row(rec)); else Reply(ctx, 404, new { error = "No such download." });
            }
            else if (parts is ["downloads", var id2, var action] && action is "pause" or "resume" && Guid.TryParse(id2, out var gid2))
            {
                if (method != "POST") { Reply(ctx, 405); return; }
                if (_manager.Get(gid2) is not { } target) { Reply(ctx, 404, new { error = "No such download." }); return; }
                var allowed = action == "pause" ? target.State is DownloadState.Queued or DownloadState.Running : target.State is DownloadState.Paused or DownloadState.Failed;
                if (!allowed) { Reply(ctx, 409, new { error = $"Can't {action} a download that is {target.State}." }); return; }
                if (action == "pause") _manager.Pause(gid2); else _manager.Resume(gid2);
                Reply(ctx, 204);
            }
            else Reply(ctx, 404, new { error = "Unknown route." });
        }
        catch (Exception e)
        {
            try { Reply(ctx, 500, new { error = e.Message }); } catch (Exception) { /* client already gone */ }
        }
    }

    async Task Add(HttpListenerContext ctx)
    {
        if (ctx.Request.ContentLength64 > MaxBody) { Reply(ctx, 413, new { error = "Request too large." }); return; }
        using var ms = new MemoryStream();
        var buf = new byte[8192];
        int n;
        while ((n = await ctx.Request.InputStream.ReadAsync(buf)) > 0)
        {
            ms.Write(buf, 0, n);
            if (ms.Length > MaxBody) { Reply(ctx, 413, new { error = "Request too large." }); return; }
        }
        string? url;
        try { url = JsonDocument.Parse(ms.ToArray()).RootElement.TryGetProperty("url", out var u) ? u.GetString() : null; }
        catch (Exception e) when (e is JsonException or InvalidOperationException) { url = null; }
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https" or "ftp" or "ftps"))
        {
            Reply(ctx, 400, new { error = "Send {\"url\": \"http(s)://...\"}." });
            return;
        }
        Reply(ctx, 202, new { id = _add(uri) });
    }

    static object Row(DownloadRecord r)
    {
        var name = r.FilePath != null ? Path.GetFileName(r.FilePath)
            : r.Request.FileName ?? Uri.UnescapeDataString(Path.GetFileName(r.Request.Url.AbsolutePath));
        double? progress = r.State == DownloadState.Completed ? 100 : r.Total is > 0 ? Math.Round(r.Downloaded * 100.0 / r.Total.Value, 1) : null;
        return new
        {
            id = r.Id, name, url = r.Request.Url.ToString(), state = r.State.ToString(), progress,
            downloaded = r.Downloaded, total = r.Total, filePath = r.State == DownloadState.Completed ? r.FilePath : null, error = r.Error,
        };
    }

    public void Dispose()
    {
        try { _listener.Close(); } catch (ObjectDisposedException) { }
    }
}
