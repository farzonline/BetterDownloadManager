namespace BDM.Core.Streams;

public enum StreamKind { Hls, Dash }

/// <summary>A stream to download: the manifest plus everything needed to fetch it the way the browser did.</summary>
public record StreamSource(
    Uri ManifestUrl,
    StreamKind Kind,
    IReadOnlyDictionary<string, string> Headers,
    string? CookieHeader,
    string? Title,
    string? ManifestBody,
    Uri? ThumbnailUrl = null);

/// <summary>One downloadable track. HLS: Playlist is the media playlist. DASH: Playlist is the manifest and Id the representation id.</summary>
public record StreamTrack(string Id, Uri Playlist);

public record StreamChoice(string Label, StreamTrack Video, StreamTrack? Audio, IReadOnlyList<StreamTrack> Subtitles);

public record StreamOptions(
    bool MergeSubtitles = false,
    TimeSpan? LivePollInterval = null,
    TimeSpan? RetryBaseDelay = null,
    int Parallel = 8,
    int MaxRetries = 5);

/// <summary>DRM or an encryption BDM cannot undo (SAMPLE-AES, Widevine, PlayReady...). BDM never bypasses DRM.</summary>
public class ProtectedContentException(string message) : Exception(message);

public class FfmpegMissingException(string message) : Exception(message);
