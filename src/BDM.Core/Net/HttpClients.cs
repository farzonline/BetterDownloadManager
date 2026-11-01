using System.Net;

namespace BDM.Core.Net;

public static class HttpClients
{
    /// <summary>HttpClient for downloads, optionally routed through a proxy profile (null = direct/system proxy).</summary>
    public static HttpClient Create(ProxyProfile? profile, CredentialVault vault)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 10,
            ConnectTimeout = TimeSpan.FromSeconds(30),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
        if (profile != null)
        {
            // [call] Surfshark's "HTTPS" proxies listen on 443 and expect TLS to the proxy.
            var scheme = profile.Kind == ProxyKind.Socks5 ? "socks5" : profile.Port == 443 ? "https" : "http";
            handler.Proxy = new WebProxy($"{scheme}://{profile.Host}:{profile.Port}")
            {
                Credentials = profile.Username == null
                    ? null
                    : new NetworkCredential(profile.Username, vault.Load(profile.VaultKey) ?? ""),
            };
            handler.UseProxy = true;
        }
        return new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(100) };
    }
}
