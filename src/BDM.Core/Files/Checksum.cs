using System.Security.Cryptography;

namespace BDM.Core.Files;

public static class Checksum
{
    public static async Task<string> ComputeAsync(string path, HashAlgorithmName alg)
    {
        using var h = IncrementalHash.CreateHash(alg);
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, true);
        var buf = new byte[1 << 16];
        int n;
        while ((n = await fs.ReadAsync(buf)) > 0) h.AppendData(buf, 0, n);
        return Convert.ToHexString(h.GetHashAndReset()).ToLowerInvariant();
    }
}
