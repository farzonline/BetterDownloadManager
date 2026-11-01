namespace BDM.Core.Net;

/// <summary>Turns a saved proxy profile into a proxy URL for tools that run outside our HttpClient (yt-dlp).</summary>
public static class ProxyUrls
{
    /// <returns>null for "no proxy" or an unknown (deleted) profile.</returns>
    public static string? For(string? profileId, ProxyProfileStore profiles, CredentialVault vault)
    {
        if (string.IsNullOrEmpty(profileId)) return null;
        var p = profiles.Load().FirstOrDefault(x => x.Id == profileId);
        if (p == null) return null;
        // socks5h: let the proxy resolve host names, so the lookups don't leak outside the tunnel.
        var scheme = p.Kind == ProxyKind.Socks5 ? "socks5h" : p.Port == 443 ? "https" : "http";
        var user = p.Username == null ? "" : Uri.EscapeDataString(p.Username) + (vault.Load(p.VaultKey) is { } pw ? ":" + Uri.EscapeDataString(pw) : "") + "@";
        return $"{scheme}://{user}{p.Host}:{p.Port}";
    }
}
