using BDM.Core.Engine;
using BDM.Core.Http;

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
    string? ExpectedSha256 = null);
