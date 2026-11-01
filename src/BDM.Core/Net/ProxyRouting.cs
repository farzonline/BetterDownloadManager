using BDM.Core.Engine;
using BDM.Core.Http;

namespace BDM.Core.Net;

public static class ProxyRouting
{
    /// <summary>Most specific wins: the download itself, then its queue, then its category, then the global default.</summary>
    public static string? Resolve(DownloadRequest r, QueueDefinition q, string? categoryProfile, string? globalProfile) =>
        r.ProxyProfileId ?? q.ProxyProfileId ?? categoryProfile ?? globalProfile;
}
