using System.Net;
using System.Net.Sockets;
using System.Text;
using BDM.Core.Ftp;
using BDM.Core.Http;

namespace BDM.Core.Tests;

public class FtpDownloaderTests
{
    /// <summary>Just enough of an FTP server for FluentFTP to log in and RETR one file with REST.</summary>
    sealed class FakeFtp : IDisposable
    {
        readonly TcpListener _ctrl = new(IPAddress.Loopback, 0);
        public byte[] Data { get; }
        public List<string> Commands { get; } = new();
        public int Port => ((IPEndPoint)_ctrl.LocalEndpoint).Port;

        public FakeFtp(byte[] data)
        {
            Data = data;
            _ctrl.Start();
            _ = Task.Run(async () => { while (true) _ = Session(await _ctrl.AcceptTcpClientAsync()); });
        }

        async Task Session(TcpClient c)
        {
            try
            {
                using var s = c.GetStream();
                using var r = new StreamReader(s, Encoding.ASCII);
                async Task W(string line) => await s.WriteAsync(Encoding.ASCII.GetBytes(line + "\r\n"));
                await W("220 fake ftp");
                long rest = 0;
                TcpListener? pasv = null;
                string? line;
                while ((line = await r.ReadLineAsync()) != null)
                {
                    lock (Commands) Commands.Add(line);
                    var sp = line.IndexOf(' ');
                    var cmd = (sp < 0 ? line : line[..sp]).ToUpperInvariant();
                    var arg = sp < 0 ? "" : line[(sp + 1)..];
                    switch (cmd)
                    {
                        case "USER": await W("331 password please"); break;
                        case "PASS": await W("230 logged in"); break;
                        case "SYST": await W("215 UNIX Type: L8"); break;
                        case "FEAT": await W("211-Features:\r\n REST STREAM\r\n SIZE\r\n EPSV\r\n211 End"); break;
                        case "OPTS": case "TYPE": await W("200 ok"); break;
                        case "PWD": await W("257 \"/\" is current directory"); break;
                        case "SIZE": await W($"213 {Data.Length}"); break;
                        case "REST": rest = long.Parse(arg); await W("350 restarting"); break;
                        case "EPSV":
                            pasv = new TcpListener(IPAddress.Loopback, 0); pasv.Start();
                            await W($"229 Entering Extended Passive Mode (|||{((IPEndPoint)pasv.LocalEndpoint).Port}|)"); break;
                        case "PASV":
                            pasv = new TcpListener(IPAddress.Loopback, 0); pasv.Start();
                            var p = ((IPEndPoint)pasv.LocalEndpoint).Port;
                            await W($"227 Entering Passive Mode (127,0,0,1,{p >> 8},{p & 255})"); break;
                        case "RETR":
                            await W("150 opening data connection");
                            using (var dc = await pasv!.AcceptTcpClientAsync())
                            {
                                await dc.GetStream().WriteAsync(Data.AsMemory((int)rest));
                            }
                            pasv.Stop();
                            await W("226 transfer complete"); break;
                        case "QUIT": await W("221 bye"); return;
                        default: await W("502 not implemented"); break;
                    }
                }
            }
            catch { /* test stub */ }
            finally { c.Dispose(); }
        }

        public void Dispose() => _ctrl.Stop();
    }

    [Fact]
    public async Task Ftp_ResumesFromPartialFile()
    {
        var data = new byte[200_000];
        new Random(5).NextBytes(data);
        using var ftp = new FakeFtp(data);
        var dir = Directory.CreateTempSubdirectory().FullName;
        await File.WriteAllBytesAsync(Path.Combine(dir, "file.bin.bdmpart"), data[..80_000]);
        var req = new DownloadRequest(new Uri($"ftp://user:pw@127.0.0.1:{ftp.Port}/file.bin"), null, null,
            new Dictionary<string, string>(), null, null, null, dir);

        var result = await new FtpDownloader().RunAsync(req, null, default);

        Assert.Equal(data, await File.ReadAllBytesAsync(result.Path));
        Assert.Contains(ftp.Commands, c => c == "REST 80000");
        Assert.Empty(Directory.GetFiles(dir, "*.bdmpart"));
    }
}
