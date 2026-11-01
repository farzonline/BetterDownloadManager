using System.Net;
using System.Net.Sockets;
using System.Text;
using BDM.Core.Data;
using BDM.Core.Engine;
using BDM.Core.Http;
using BDM.Core.Net;

namespace BDM.Core.Tests;

public class ProxyTests
{
    static string Tmp() => Directory.CreateTempSubdirectory().FullName;

    /// <summary>Minimal SOCKS5 server with username/password auth that relays to the requested target.</summary>
    sealed class FakeSocks5 : IDisposable
    {
        readonly TcpListener _l = new(IPAddress.Loopback, 0);
        public int Port => ((IPEndPoint)_l.LocalEndpoint).Port;
        public string? User, Pass, TargetHost;
        public int TargetPort;

        public FakeSocks5() { _l.Start(); _ = Task.Run(async () => { while (true) _ = Handle(await _l.AcceptTcpClientAsync()); }); }

        static async Task<byte[]> Read(Stream s, int n)
        {
            var b = new byte[n]; var got = 0;
            while (got < n) { var r = await s.ReadAsync(b.AsMemory(got, n - got)); if (r == 0) throw new EndOfStreamException(); got += r; }
            return b;
        }

        async Task Handle(TcpClient c)
        {
            try
            {
                var s = c.GetStream();
                var hello = await Read(s, 2); await Read(s, hello[1]);
                await s.WriteAsync(new byte[] { 5, 2 });
                var auth = await Read(s, 2); User = Encoding.UTF8.GetString(await Read(s, auth[1]));
                var plen = (await Read(s, 1))[0]; Pass = Encoding.UTF8.GetString(await Read(s, plen));
                await s.WriteAsync(new byte[] { 1, 0 });
                var req = await Read(s, 4);
                if (req[3] == 1) { var a = await Read(s, 4); TargetHost = new IPAddress(a).ToString(); }
                else { var len = (await Read(s, 1))[0]; TargetHost = Encoding.ASCII.GetString(await Read(s, len)); }
                var p = await Read(s, 2); TargetPort = p[0] << 8 | p[1];
                await s.WriteAsync(new byte[] { 5, 0, 0, 1, 0, 0, 0, 0, 0, 0 });
                using var up = new TcpClient();
                await up.ConnectAsync(TargetHost == "localhost" ? "127.0.0.1" : TargetHost, TargetPort);
                var us = up.GetStream();
                await Task.WhenAny(s.CopyToAsync(us), us.CopyToAsync(s));
            }
            catch { /* test stub */ }
            finally { c.Dispose(); }
        }
        public void Dispose() => _l.Stop();
    }

    /// <summary>HTTP proxy that rejects everything with 407.</summary>
    sealed class Fake407Proxy : IDisposable
    {
        readonly TcpListener _l = new(IPAddress.Loopback, 0);
        public int Port => ((IPEndPoint)_l.LocalEndpoint).Port;
        public Fake407Proxy()
        {
            _l.Start();
            _ = Task.Run(async () => { while (true) _ = Reply(await _l.AcceptTcpClientAsync()); });
        }
        static async Task Reply(TcpClient c)
        {
            try
            {
                var s = c.GetStream();
                var buf = new byte[4096];
                while (true)
                {
                    var n = await s.ReadAsync(buf);
                    if (n == 0) break;
                    await s.WriteAsync(Encoding.ASCII.GetBytes(
                        "HTTP/1.1 407 Proxy Authentication Required\r\nProxy-Authenticate: Basic realm=\"x\"\r\nContent-Length: 0\r\n\r\n"));
                }
            }
            catch { }
            finally { c.Dispose(); }
        }
        public void Dispose() => _l.Stop();
    }

    [Fact]
    public void Vault_RoundTrips_AndFileIsNotPlaintext()
    {
        var path = Path.Combine(Tmp(), "vault.json");
        new CredentialVault(path).Save("proxy:a", "s3cr3t-pass-word");
        Assert.Equal("s3cr3t-pass-word", new CredentialVault(path).Load("proxy:a"));
        Assert.Null(new CredentialVault(path).Load("missing"));
        Assert.DoesNotContain("s3cr3t-pass-word", File.ReadAllText(path));
    }

    [Fact]
    public void Routing_PrecedenceDownloadOverQueueOverCategoryOverGlobal()
    {
        var req = new DownloadRequest(new Uri("http://x/"), null, null, new Dictionary<string, string>(), null, "dl", null, ".");
        var q = QueueDefinition.Main with { ProxyProfileId = "queue" };
        Assert.Equal("dl", ProxyRouting.Resolve(req, q, "cat", "glob"));
        Assert.Equal("queue", ProxyRouting.Resolve(req with { ProxyProfileId = null }, q, "cat", "glob"));
        Assert.Equal("cat", ProxyRouting.Resolve(req with { ProxyProfileId = null }, QueueDefinition.Main, "cat", "glob"));
        Assert.Equal("glob", ProxyRouting.Resolve(req with { ProxyProfileId = null }, QueueDefinition.Main, null, "glob"));
        Assert.Null(ProxyRouting.Resolve(req with { ProxyProfileId = null }, QueueDefinition.Main, null, null));
    }

    [Fact]
    public void Create_SendsABrowserUserAgentByDefault()
    {
        // Video CDNs answer 403 to requests without a browser User-Agent (seen on a real vdcdn.xyz link).
        using var http = HttpClients.Create(null, new CredentialVault(Path.Combine(Tmp(), "v.json")));
        var ua = http.DefaultRequestHeaders.UserAgent.ToString();
        Assert.StartsWith("Mozilla/5.0 (Windows NT 10.0; Win64; x64)", ua);
        Assert.Contains("Chrome/", ua);
    }

    [Fact]
    public async Task Create_Socks5Profile_RoutesThroughLocalFakeProxy()
    {
        await using var server = TestServer.Start(30_000);
        using var socks = new FakeSocks5();
        var vault = new CredentialVault(Path.Combine(Tmp(), "v.json"));
        var profile = new ProxyProfile("p1", "Test", ProxyKind.Socks5, "127.0.0.1", socks.Port, "bob");
        vault.Save(profile.VaultKey, "pw");
        using var http = HttpClients.Create(profile, vault);

        var bytes = await http.GetByteArrayAsync(server.FileUrl);

        Assert.Equal(server.Body, bytes);
        Assert.Equal("bob", socks.User);
        Assert.Equal("pw", socks.Pass);
        Assert.Equal(server.BaseUri.Port, socks.TargetPort);
    }

    [Fact]
    public async Task Create_WrongCredentials_Surfaces407AsNetworkFailureWithMessage()
    {
        using var proxy = new Fake407Proxy();
        var vault = new CredentialVault(Path.Combine(Tmp(), "v.json"));
        var profile = new ProxyProfile("p2", "Bad", ProxyKind.Http, "127.0.0.1", proxy.Port, "bob");
        vault.Save(profile.VaultKey, "wrong");
        var http = HttpClients.Create(profile, vault);
        var store = new DownloadStore(Path.Combine(Tmp(), "bdm.db"));
        var m = new DownloadManager(store, _ => http);
        var req = new DownloadRequest(new Uri("http://target.invalid/file"), null, null, new Dictionary<string, string>(), null, null, null, Tmp());
        var id = m.Add(req);

        DownloadRecord? r = null;
        for (var i = 0; i < 400 && r?.State != DownloadState.Failed; i++) { await Task.Delay(25); r = m.Get(id); }

        Assert.Equal(DownloadState.Failed, r!.State);
        Assert.Equal(DownloadFailure.Network, r.Failure);
        Assert.Contains("407", r.Error);
        Assert.Contains("proxy", r.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Import_ParsesBothLineFormats_SkipsComments()
    {
        var list = SurfsharkImport.Parse("# my servers\n\nus-nyc.example.com:443\nMy UK,uk-lon.example.com,1080\n  \n");
        Assert.Equal(2, list.Count);
        Assert.Equal("us-nyc.example.com", list[0].Host);
        Assert.Equal(443, list[0].Port);
        Assert.Equal(ProxyKind.Http, list[0].Kind);
        Assert.Equal("My UK", list[1].Name);
        Assert.Equal(ProxyKind.Socks5, list[1].Kind);
        Assert.Null(list[1].Username);
    }
}
