using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using BDM.Core.Api;

namespace BDM.Core.Net;

/// <summary>
/// Lets BDM's engine download through the browser, for sites whose bot protection (Cloudflare and the like) only
/// serves a real browser. Downloads routed through <see cref="ProfileId"/> use <see cref="Handler"/>: each request is
/// queued here, the BDM extension picks it up (GET /relay/next), fetches it inside Chrome with the owner's own
/// session, and streams the answer back (POST /relay/{id}/head, /data, /end). Segments, resume and retries all
/// work as usual because the engine only sees an ordinary HttpClient.
/// Loopback only, guarded by a per-run token, a Host check (DNS rebinding) and an Origin check (web pages).
/// </summary>
public sealed class BrowserRelay : IDisposable
{
    /// <summary>The "proxy profile" that routes a download through the browser.</summary>
    public const string ProfileId = "browser";
    /// <summary>The BDM extension's id (fixed by the key in its manifest).</summary>
    public const string ExtensionId = "depjhibopdmbilmmphmipdklabicghkf";
    const string ExtensionOrigin = "chrome-extension://" + ExtensionId;
    const int MaxChunk = 16 * 1024 * 1024;
    static readonly TimeSpan HeadTimeout = TimeSpan.FromSeconds(60), IdleTimeout = TimeSpan.FromSeconds(90);
    static readonly HashSet<string> Dropped = new(StringComparer.OrdinalIgnoreCase) { "content-encoding", "transfer-encoding", "connection", "set-cookie" };

    readonly HttpListener _listener = new();
    readonly byte[] _token;
    readonly TimeSpan _pickupTimeout;
    readonly Channel<Job> _queue = Channel.CreateUnbounded<Job>();
    readonly ConcurrentDictionary<string, Job> _jobs = new();

    public int Port { get; }
    public string Token { get; }
    public HttpMessageHandler Handler { get; }

    /// <param name="pickupTimeout">How long a request waits for the browser to take it before failing (browser closed).</param>
    public BrowserRelay(string token, int port, TimeSpan pickupTimeout)
    {
        Token = token;
        _token = Encoding.UTF8.GetBytes(token);
        Port = port;
        _pickupTimeout = pickupTimeout;
        _listener.Prefixes.Add($"http://{RestApi.ListenAddress}:{port}/");
        Handler = new RelayHandler(this);
    }

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
                _ = Task.Run(() => HandleAsync(ctx));
            }
        });
    }

    public void Dispose()
    {
        _listener.Close();
        foreach (var j in _jobs.Values) j.Fail("BDM closed.");
    }

    sealed class Job(HttpRequestMessage request)
    {
        public string Id { get; } = Guid.NewGuid().ToString("N");
        public HttpRequestMessage Request { get; } = request;
        public TaskCompletionSource PickedUp { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<HttpResponseMessage> Head { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        /// <summary>Body chunks; bounded so a slow disk slows the browser down instead of filling memory.</summary>
        public Channel<byte[]> Body { get; } = Channel.CreateBounded<byte[]>(4);
        public volatile bool Abandoned;

        public void Fail(string why)
        {
            Abandoned = true;
            Head.TrySetException(new HttpRequestException(why));
            Body.Writer.TryComplete(new IOException(why));
        }
    }

    sealed class RelayHandler(BrowserRelay relay) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var job = new Job(request);
            relay._jobs[job.Id] = job;
            relay._queue.Writer.TryWrite(job);
            try
            {
                try { await job.PickedUp.Task.WaitAsync(relay._pickupTimeout, ct); }
                catch (TimeoutException)
                {
                    throw new HttpRequestException("This site only lets your browser download the file, and your browser isn't open (or the BDM extension is off). Open it, then resume.");
                }
                try { return await job.Head.Task.WaitAsync(HeadTimeout, ct); }
                catch (TimeoutException) { throw new HttpRequestException("Your browser took the request but the site didn't answer."); }
            }
            catch
            {
                job.Abandoned = true;
                relay._jobs.TryRemove(job.Id, out _);
                throw;
            }
        }
    }

    /// <summary>The body as the engine reads it; closing it (pause, cancel) tells the browser to stop.</summary>
    sealed class BodyStream(Job job, Action closed) : Stream
    {
        byte[]? _chunk;
        int _pos;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            while (_chunk == null || _pos == _chunk.Length)
            {
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
                idle.CancelAfter(IdleTimeout);
                try
                {
                    if (!await job.Body.Reader.WaitToReadAsync(idle.Token)) return 0;
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new IOException("The browser stopped sending this file (was it closed?).");
                }
                job.Body.Reader.TryRead(out _chunk);
                _pos = 0;
            }
            var n = Math.Min(buffer.Length, _chunk.Length - _pos);
            _chunk.AsMemory(_pos, n).CopyTo(buffer);
            _pos += n;
            return n;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        protected override void Dispose(bool disposing)
        {
            if (disposing) closed();
            base.Dispose(disposing);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // ------------------------------------------------------------------ the extension's side

    record Head(int Status, Dictionary<string, string>? Headers);
    record End(string? Error);
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    async Task HandleAsync(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        var res = ctx.Response;
        try
        {
            if (req.Headers["Host"] != $"{RestApi.ListenAddress}:{Port}") { Reply(res, 421); return; }
            var origin = req.Headers["Origin"];
            if (origin != null && origin != ExtensionOrigin) { Reply(res, 403); return; }
            if (origin != null)
            {
                res.AddHeader("Access-Control-Allow-Origin", origin);
                if (req.HttpMethod == "OPTIONS")
                {
                    res.AddHeader("Access-Control-Allow-Headers", "X-BDM-Token, Content-Type");
                    res.AddHeader("Access-Control-Allow-Methods", "GET, POST");
                    Reply(res, 204);
                    return;
                }
            }
            if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(req.Headers["X-BDM-Token"] ?? ""), _token)) { Reply(res, 401); return; }

            var parts = req.Url!.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            switch (req.HttpMethod, parts)
            {
                case ("GET", ["relay", "next"]): await NextAsync(ctx); return;
                case ("POST", ["relay", var id, var what]) when _jobs.TryGetValue(id, out var job) && !job.Abandoned:
                    await JobUpdateAsync(ctx, job, what);
                    return;
                case ("POST", ["relay", _, _]): Reply(res, 410); return; // gone: BDM no longer wants it
                default: Reply(res, 404); return;
            }
        }
        catch (Exception e) when (e is HttpListenerException or IOException or ObjectDisposedException or JsonException or InvalidOperationException)
        {
            try { Reply(res, 400); } catch (Exception) { /* connection already gone */ }
        }
    }

    /// <summary>Long poll: the next request for the browser, or 204 after <c>wait</c> seconds (at most 25).</summary>
    async Task NextAsync(HttpListenerContext ctx)
    {
        var wait = int.TryParse(ctx.Request.QueryString["wait"], out var w) ? Math.Clamp(w, 0, 25) : 20;
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(wait));
        while (true)
        {
            Job job;
            try
            {
                if (!_queue.Reader.TryRead(out job!)) job = await _queue.Reader.ReadAsync(limit.Token);
            }
            catch (OperationCanceledException) { Reply(ctx.Response, 204); return; }
            if (job.Abandoned) continue;
            job.PickedUp.TrySetResult();
            var r = job.Request;
            var range = r.Headers.Range?.ToString();
            var body = JsonSerializer.SerializeToUtf8Bytes(new { id = job.Id, method = r.Method.Method, url = r.RequestUri!.AbsoluteUri, range }, Json);
            ctx.Response.ContentType = "application/json";
            ctx.Response.ContentLength64 = body.Length;
            await ctx.Response.OutputStream.WriteAsync(body);
            ctx.Response.Close();
            return;
        }
    }

    async Task JobUpdateAsync(HttpListenerContext ctx, Job job, string what)
    {
        var res = ctx.Response;
        switch (what)
        {
            case "head":
            {
                var head = await JsonSerializer.DeserializeAsync<Head>(ctx.Request.InputStream, Json) ?? throw new JsonException("empty head");
                var msg = new HttpResponseMessage((HttpStatusCode)head.Status) { RequestMessage = job.Request };
                msg.Content = new StreamContent(new BodyStream(job, () => { job.Abandoned = true; job.Body.Writer.TryComplete(); _jobs.TryRemove(job.Id, out _); }));
                var headers = head.Headers ?? new();
                var decoded = headers.Keys.Any(k => k.Equals("content-encoding", StringComparison.OrdinalIgnoreCase));
                foreach (var (k, v) in headers)
                {
                    // The browser already undid any compression, so the original length no longer matches.
                    if (Dropped.Contains(k) || (decoded && k.Equals("content-length", StringComparison.OrdinalIgnoreCase))) continue;
                    if (!msg.Headers.TryAddWithoutValidation(k, v)) msg.Content.Headers.TryAddWithoutValidation(k, v);
                }
                if (job.Request.Method == HttpMethod.Head) job.Body.Writer.TryComplete();
                job.Head.TrySetResult(msg);
                Reply(res, 204);
                return;
            }
            case "data":
            {
                if (ctx.Request.ContentLength64 > MaxChunk) { Reply(res, 413); return; }
                var ms = new MemoryStream();
                await ctx.Request.InputStream.CopyToAsync(ms);
                try { await job.Body.Writer.WriteAsync(ms.ToArray()); }
                catch (ChannelClosedException) { Reply(res, 410); return; } // BDM stopped reading (paused or done with this part)
                Reply(res, 204);
                return;
            }
            case "end":
            {
                var end = ctx.Request.HasEntityBody ? await JsonSerializer.DeserializeAsync<End>(ctx.Request.InputStream, Json) : null;
                if (end?.Error is { Length: > 0 } error) job.Fail("Your browser couldn't fetch it: " + error);
                else if (!job.Head.Task.IsCompleted) job.Fail("Your browser ended the request without an answer.");
                else job.Body.Writer.TryComplete();
                _jobs.TryRemove(job.Id, out _);
                Reply(res, 204);
                return;
            }
            default:
                Reply(res, 404);
                return;
        }
    }

    static void Reply(HttpListenerResponse res, int status)
    {
        res.StatusCode = status;
        res.Close();
    }
}
