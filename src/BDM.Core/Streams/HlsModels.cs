namespace BDM.Core.Streams;

public record HlsKey(string Method, Uri? Uri, byte[]? Iv)
{
    /// <summary>NONE and AES-128 are handled; SAMPLE-AES (and anything else) is not.</summary>
    public bool IsSupported => Method is "NONE" or "AES-128";

    /// <summary>The explicit IV, or the segment's media sequence number as a 128-bit big-endian value (RFC 8216 §5.2).</summary>
    public byte[] IvFor(long sequence)
    {
        if (Iv != null) return Iv;
        var iv = new byte[16];
        for (var i = 0; i < 8; i++) iv[15 - i] = (byte)(sequence >> (8 * i));
        return iv;
    }
}

public record HlsMap(Uri Url, long? ByteRangeOffset, long? ByteRangeLength);

public record HlsSegment(Uri Url, double Duration, long Sequence, long? ByteRangeOffset, long? ByteRangeLength, HlsKey? Key);

public record HlsVariant(Uri Url, int Bandwidth, string? Resolution, string? Codecs, string? AudioGroup, string? SubtitleGroup);

public record HlsMedia(string Type, string GroupId, Uri? Url, string? Language, string? Name);

public record HlsPlaylist(
    bool IsMaster,
    IReadOnlyList<HlsVariant> Variants,
    IReadOnlyList<HlsMedia> Media,
    IReadOnlyList<HlsSegment> Segments,
    HlsMap? Map,
    bool Ended,
    double TargetDuration);
