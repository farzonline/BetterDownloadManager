using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using BDM.Core.Ipc;
using BDM.Host;
using Microsoft.Win32;

namespace BDM.Host.Tests;

public class HostTests
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    static string UniquePipe() => "bdm-test-" + Guid.NewGuid().ToString("N");

    /// <summary>Stands in for the running BDM app: one JSON line in, one JSON line out.</summary>
    sealed class FakeAppPipe : IAsyncDisposable
    {
        readonly string _name;
        readonly CancellationTokenSource _cts = new();
        public List<HostMessage> Received { get; } = new();

        public FakeAppPipe(string name)
        {
            _name = name;
            _ = Task.Run(async () =>
            {
                while (!_cts.IsCancellationRequested)
                {
                    try
                    {
                        await using var s = new NamedPipeServerStream(_name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                        await s.WaitForConnectionAsync(_cts.Token);
                        using var r = new StreamReader(s, Encoding.UTF8, leaveOpen: true);
                        var line = await r.ReadLineAsync();
                        if (string.IsNullOrEmpty(line)) continue; // a liveness probe that connected and left
                        lock (Received) Received.Add(JsonSerializer.Deserialize<HostMessage>(line, Json)!);
                        var w = new StreamWriter(s, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
                        await w.WriteLineAsync(JsonSerializer.Serialize(new HostReply(true, null, false), Json));
                        s.WaitForPipeDrain();
                    }
                    catch (OperationCanceledException) { }
                    catch (IOException) { }
                }
            });
        }
        public ValueTask DisposeAsync() { _cts.Cancel(); return ValueTask.CompletedTask; }
    }

    static MemoryStream Framed(params HostMessage[] msgs)
    {
        var ms = new MemoryStream();
        foreach (var m in msgs) NativeMessaging.WriteAsync(ms, JsonSerializer.Serialize(m, Json)).GetAwaiter().GetResult();
        ms.Position = 0;
        return ms;
    }

    static async Task<List<HostReply>> ReadReplies(MemoryStream stdout)
    {
        stdout.Position = 0;
        var list = new List<HostReply>();
        while (await NativeMessaging.ReadAsync(stdout) is { } j) list.Add(JsonSerializer.Deserialize<HostReply>(j, Json)!);
        return list;
    }

    static HostMessage Add(string url) => new("add", url, "http://page/", "Title", new() { ["Referer"] = "http://page/" }, "a=b", null, null);

    [Fact]
    public async Task Frame_RoundTrips_UnicodeAndLargeMessage()
    {
        var text = "héllo €" + new string('x', 1_000_000);
        var ms = new MemoryStream();
        await NativeMessaging.WriteAsync(ms, text);
        ms.Position = 0;
        Assert.Equal(text, await NativeMessaging.ReadAsync(ms));
    }

    [Fact]
    public async Task Read_ReturnsNullOnEof() => Assert.Null(await NativeMessaging.ReadAsync(new MemoryStream()));

    [Fact]
    public async Task Host_Add_ForwardsToPipeAndRepliesOk()
    {
        var name = UniquePipe();
        await using var app = new FakeAppPipe(name);
        var stdout = new MemoryStream();
        var link = new PipeAppLink(name, "unused.exe", TimeSpan.FromSeconds(2), _ => throw new InvalidOperationException("must not launch"));

        await new HostRunner(link).RunAsync(Framed(Add("http://x/a.zip")), stdout, default);

        var reply = Assert.Single(await ReadReplies(stdout));
        Assert.True(reply.Ok);
        Assert.False(reply.AppStarted);
        Assert.Equal("http://x/a.zip", Assert.Single(app.Received).Url);
        Assert.Equal("a=b", app.Received[0].Cookie);
    }

    [Fact]
    public async Task Host_AppNotRunning_LaunchesAppThenForwards()
    {
        var name = UniquePipe();
        FakeAppPipe? app = null;
        var launches = 0;
        var link = new PipeAppLink(name, "bdm.exe", TimeSpan.FromSeconds(5), exe => { launches++; app = new FakeAppPipe(name); });
        var stdout = new MemoryStream();

        await new HostRunner(link).RunAsync(Framed(Add("http://x/a.zip")), stdout, default);

        var reply = Assert.Single(await ReadReplies(stdout));
        Assert.True(reply.Ok);
        Assert.True(reply.AppStarted);
        Assert.Equal(1, launches);
        Assert.Single(app!.Received);
        await app.DisposeAsync();
    }

    [Fact]
    public async Task Host_AppFailsToStart_RepliesOkFalse()
    {
        var link = new PipeAppLink(UniquePipe(), "bdm.exe", TimeSpan.FromMilliseconds(600), _ => { });
        var stdout = new MemoryStream();

        await new HostRunner(link).RunAsync(Framed(Add("http://x/a.zip")), stdout, default);

        var reply = Assert.Single(await ReadReplies(stdout));
        Assert.False(reply.Ok);
        Assert.False(string.IsNullOrWhiteSpace(reply.Error));
    }

    [Fact]
    public async Task Host_Ping_AnswersWithoutTouchingTheApp()
    {
        var link = new PipeAppLink(UniquePipe(), "bdm.exe", TimeSpan.FromMilliseconds(100), _ => throw new InvalidOperationException("must not launch"));
        var stdout = new MemoryStream();
        await new HostRunner(link).RunAsync(Framed(new HostMessage("ping", null, null, null, null, null, null, null)), stdout, default);
        Assert.True(Assert.Single(await ReadReplies(stdout)).Ok);
    }

    [Fact]
    public async Task Host_GarbageInput_RepliesErrorAndKeepsRunning()
    {
        var ms = new MemoryStream();
        await NativeMessaging.WriteAsync(ms, "{not json");
        await NativeMessaging.WriteAsync(ms, JsonSerializer.Serialize(new HostMessage("ping", null, null, null, null, null, null, null), Json));
        ms.Position = 0;
        var stdout = new MemoryStream();
        var link = new PipeAppLink(UniquePipe(), "bdm.exe", TimeSpan.FromMilliseconds(100), _ => { });
        await new HostRunner(link).RunAsync(ms, stdout, default);
        var replies = await ReadReplies(stdout);
        Assert.False(replies[0].Ok);
        Assert.True(replies[1].Ok);
    }

    [Fact]
    public void Registrar_WritesManifestWithAllowedOriginAndRegistryKeys()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var exe = Path.Combine(dir, "bdm-host.exe");
        File.WriteAllText(exe, "x");
        var root = @"Software\BDMTest_" + Guid.NewGuid().ToString("N");
        try
        {
            HostRegistrar.Register(exe, "abcdefghijklmnopabcdefghijklmnop", new[] { "chrome", "edge", "brave" }, root);

            var manifestPath = Path.Combine(dir, "com.bdm.host.json");
            using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
            Assert.Equal("com.bdm.host", doc.RootElement.GetProperty("name").GetString());
            Assert.Equal(exe, doc.RootElement.GetProperty("path").GetString());
            Assert.Equal("stdio", doc.RootElement.GetProperty("type").GetString());
            Assert.Equal("chrome-extension://abcdefghijklmnopabcdefghijklmnop/", doc.RootElement.GetProperty("allowed_origins")[0].GetString());
            foreach (var sub in new[] { @"Google\Chrome", @"Microsoft\Edge", @"BraveSoftware\Brave-Browser" })
            {
                using var k = Registry.CurrentUser.OpenSubKey($@"{root}\{sub}\NativeMessagingHosts\com.bdm.host");
                Assert.Equal(manifestPath, k!.GetValue(null));
            }
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(root, throwOnMissingSubKey: false); }
    }
}
