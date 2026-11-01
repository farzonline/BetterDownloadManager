using System.Net;
using BDM.Core.Engine;
using BDM.Core.Files;
using BDM.Core.Http;
using FluentFTP;
using FluentFTP.Exceptions;

namespace BDM.Core.Ftp;

/// <summary>FTP/FTPS download with resume (REST) into a .bdmpart file, then rename.</summary>
public sealed class FtpDownloader
{
    public async Task<DownloadResult> RunAsync(DownloadRequest r, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        var u = r.Url;
        var cred = u.UserInfo.Length > 0
            ? new NetworkCredential(Uri.UnescapeDataString(u.UserInfo.Split(':')[0]), u.UserInfo.Contains(':') ? Uri.UnescapeDataString(u.UserInfo.Split(':', 2)[1]) : "")
            : new NetworkCredential("anonymous", "bdm@localhost");
        var remote = Uri.UnescapeDataString(u.AbsolutePath);
        var name = FileNaming.Sanitize(r.FileName ?? Path.GetFileName(remote));
        Directory.CreateDirectory(r.DestinationDir);
        var part = Path.Combine(r.DestinationDir, name + ".bdmpart");

        var secure = u.Scheme == "ftps";
        var port = u.Port > 0 ? u.Port : secure ? 990 : 21;
        using var client = new AsyncFtpClient(u.Host, cred, port);
        client.Config.EncryptionMode = secure ? FtpEncryptionMode.Implicit : FtpEncryptionMode.None;
        client.Config.DataConnectionType = FtpDataConnectionType.AutoPassive;
        try
        {
            await client.Connect(ct);
            var size = await client.GetFileSize(remote, -1, ct);
            var prog = new Progress<FtpProgress>(p =>
                progress?.Report(new DownloadProgress(p.TransferredBytes, size >= 0 ? size : null, p.TransferSpeed, Array.Empty<Segment>())));
            var status = await client.DownloadFile(part, remote, FtpLocalExists.Resume, FtpVerify.None, prog, ct);
            if (status == FtpStatus.Failed) throw new DownloadException(DownloadFailure.Network, "The FTP transfer failed.");
        }
        catch (FtpException e)
        {
            throw new DownloadException(DownloadFailure.Network, "FTP error: " + e.Message, e);
        }

        var final = Path.Combine(r.DestinationDir, FileNaming.Unique(r.DestinationDir, name));
        File.Move(part, final);
        return new DownloadResult(final, new FileInfo(final).Length);
    }
}
