using System.Text.Json;

namespace BDM.Core.Engine;

public static class SegmentSidecar
{
    record Dto(long Length, string? ETag, string? LastModified, List<long[]> Segments);

    /// <summary>
    /// Writes the resume file. Returns false when another program (antivirus, indexer, backup) holds it: the next save,
    /// a second later, catches up, so this never fails the download.
    /// </summary>
    public static bool Save(string path, SegmentMap map, string? etag, string? lastModified)
    {
        var dto = new Dto(map.Length, etag, lastModified,
            map.Snapshot().Select(s => new[] { s.Start, s.End, Math.Min(Interlocked.Read(ref s.Downloaded), s.End - s.Start + 1) }).ToList());
        var tmp = path + ".tmp";
        try
        {
            File.WriteAllText(tmp, JsonSerializer.Serialize(dto));
            File.Move(tmp, path, overwrite: true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Error("Could not save the resume file (another program has it open); trying again shortly", e);
            return false;
        }
    }

    public static (SegmentMap Map, string? ETag, string? LastModified)? Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var dto = JsonSerializer.Deserialize<Dto>(File.ReadAllText(path));
            if (dto == null) return null;
            var map = new SegmentMap(dto.Length,
                dto.Segments.Select(a => new Segment { Start = a[0], End = a[1], Downloaded = a[2] }));
            return map.IsContiguous() ? (map, dto.ETag, dto.LastModified) : null;
        }
        catch (Exception e) when (e is JsonException or IOException or IndexOutOfRangeException) { return null; }
    }
}
