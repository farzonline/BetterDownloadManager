using System.Text.Json;

namespace BDM.Core.Engine;

public static class SegmentSidecar
{
    record Dto(long Length, string? ETag, string? LastModified, List<long[]> Segments);

    public static void Save(string path, SegmentMap map, string? etag, string? lastModified)
    {
        var dto = new Dto(map.Length, etag, lastModified,
            map.Segments.Select(s => new[] { s.Start, s.End, Interlocked.Read(ref s.Downloaded) }).ToList());
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(dto));
        File.Move(tmp, path, overwrite: true);
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
