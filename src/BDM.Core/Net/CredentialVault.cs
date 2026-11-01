using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BDM.Core.Net;

/// <summary>Secrets encrypted with Windows DPAPI (current user), stored as JSON key → base64 ciphertext.</summary>
public sealed class CredentialVault(string path)
{
    readonly object _gate = new();

    Dictionary<string, string> Read()
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? new() : new(); }
        catch (JsonException) { return new(); }
    }

    public void Save(string key, string secret)
    {
        lock (_gate)
        {
            var d = Read();
            d[key] = Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), null, DataProtectionScope.CurrentUser));
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.WriteAllText(path, JsonSerializer.Serialize(d));
        }
    }

    public string? Load(string key)
    {
        lock (_gate)
        {
            if (!Read().TryGetValue(key, out var b64)) return null;
            try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(b64), null, DataProtectionScope.CurrentUser)); }
            catch (CryptographicException) { return null; } // written by another Windows user
        }
    }
}
