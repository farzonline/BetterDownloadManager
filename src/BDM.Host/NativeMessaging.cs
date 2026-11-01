using System.Buffers.Binary;
using System.Text;

namespace BDM.Host;

/// <summary>Chrome native messaging framing: 4-byte little-endian length, then UTF-8 JSON.</summary>
public static class NativeMessaging
{
    /// <summary>Returns null at end of stream (the browser closed the port).</summary>
    public static async Task<string?> ReadAsync(Stream s)
    {
        var header = new byte[4];
        if (!await ReadFullAsync(s, header)) return null;
        var len = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (len > 64 * 1024 * 1024) throw new InvalidDataException("Message too large.");
        var body = new byte[len];
        if (!await ReadFullAsync(s, body)) return null;
        return Encoding.UTF8.GetString(body);
    }

    public static async Task WriteAsync(Stream s, string json)
    {
        var body = Encoding.UTF8.GetBytes(json);
        var header = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)body.Length);
        await s.WriteAsync(header);
        await s.WriteAsync(body);
        await s.FlushAsync();
    }

    static async Task<bool> ReadFullAsync(Stream s, byte[] buf)
    {
        var got = 0;
        while (got < buf.Length)
        {
            var n = await s.ReadAsync(buf.AsMemory(got));
            if (n == 0) return false;
            got += n;
        }
        return true;
    }
}
