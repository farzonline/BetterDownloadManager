using BDM.Core.Engine;
using BDM.Core.Http;
using BDM.Core.Streams;

namespace BDM.Core.Data;

public enum DownloadState { Queued, Running, Paused, Completed, Failed }

/// <summary>
/// Persisted view of one download. FilePath is the target path while unfinished and the real path once Completed.
/// Failure marks why a Failed download stopped (AuthExpired = progress kept, needs a fresh link).
/// </summary>
public record DownloadRecord(
    Guid Id,
    DownloadRequest Request,
    DownloadState State,
    long Downloaded,
    long? Total,
    string? Error,
    string QueueId,
    DateTime Added,
    DownloadFailure? Failure = null,
    string? FilePath = null,
    bool Duplicate = false,
    string? ExpectedSha256 = null,
    StreamSource? Stream = null,
    StreamChoice? StreamChoice = null,
    string? YtDlpFormat = null);

/// <summary>Session secrets (cookies, Authorization) are only needed while a download can still be retried.</summary>
public static class RecordSecrets
{
    static readonly string[] SecretHeaders = ["Authorization", "Cookie", "Proxy-Authorization"];

    static Dictionary<string, string> Strip(IEnumerable<KeyValuePair<string, string>> headers) =>
        headers.Where(h => !SecretHeaders.Contains(h.Key, StringComparer.OrdinalIgnoreCase)).ToDictionary(h => h.Key, h => h.Value);

    public static DownloadRecord WithoutSecrets(this DownloadRecord r) => r with
    {
        Request = r.Request with { CookieHeader = null, Headers = Strip(r.Request.Headers) },
        Stream = r.Stream == null ? null : r.Stream with { CookieHeader = null, Headers = Strip(r.Stream.Headers) },
    };
}
