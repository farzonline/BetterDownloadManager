using System.Net;
using System.Net.Sockets;
using System.Text;
using BDM.Core.Ftp;
using BDM.Core.Http;

namespace BDM.Core.Tests;

public class FtpDownloaderTests
{
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
