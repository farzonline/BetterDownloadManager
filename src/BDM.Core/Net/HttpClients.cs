using System.Net;

namespace BDM.Core.Net;

public static class HttpClients
{
    /// <summary>Sent when the browser extension did not pass its own; many video CDNs refuse requests without one.</summary>
    public const string BrowserUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/141.0.0.0 Safari/537.36";

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
        var http = new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(100) };
        // A request's own User-Agent header (from the browser) wins: default headers only fill in what is missing.
        http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", BrowserUserAgent);
        return http;
    }
}
