using System.Text.Json;
using System.Text.Json.Serialization;

namespace BDM.Core.Net;

/// <summary>The saved proxy / "VPN" profiles (no passwords: those are in the CredentialVault).</summary>
public sealed class ProxyProfileStore(string path)
{
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public IReadOnlyList<ProxyProfile> Load()
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<List<ProxyProfile>>(File.ReadAllText(path), Json) ?? new() : new(); }
        catch (Exception e) when (e is JsonException or IOException) { return new List<ProxyProfile>(); }
    }

    public void Save(IEnumerable<ProxyProfile> profiles)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(profiles.ToList(), Json));
    }
}
