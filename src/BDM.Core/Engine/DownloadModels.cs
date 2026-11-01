namespace BDM.Core.Engine;

public record DownloadOptions(
    int Connections = 8,
    int MaxRetries = 5,
    long? SpeedLimitBytesPerSec = null,
    TimeSpan? RetryBaseDelay = null,
    long MinSplitBytes = 256 * 1024);

public record DownloadProgress(long Downloaded, long? Total, double BytesPerSec, IReadOnlyList<Segment> Segments);

public record DownloadResult(string Path, long Bytes);

public enum DownloadFailure { Network, AuthExpired, DiskFull, ServerChanged, Protected, Other }

public class DownloadException(DownloadFailure kind, string message, Exception? inner = null) : Exception(message, inner)
{
    public DownloadFailure Kind { get; } = kind;

    // ERROR_DISK_FULL / ERROR_HANDLE_DISK_FULL
    public static DownloadException FromIo(IOException e) =>
        e.HResult is unchecked((int)0x80070070) or unchecked((int)0x80070027)
            ? new DownloadException(DownloadFailure.DiskFull, "The disk is full.", e)
            : new DownloadException(DownloadFailure.Network, "File write failed: " + e.Message, e);
}
