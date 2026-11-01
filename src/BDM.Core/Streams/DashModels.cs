namespace BDM.Core.Streams;

public record DashRepresentation(
    string Id,
    string MimeType,
    string? Codecs,
    int Bandwidth,
    int? Width,
    int? Height,
    string? Language,
    Uri? InitUrl,
    IReadOnlyList<Uri> SegmentUrls,
    double DurationSeconds,
    bool Protected);

public record DashPeriod(IReadOnlyList<DashRepresentation> Representations);

public record DashManifest(bool IsDynamic, IReadOnlyList<DashPeriod> Periods);
