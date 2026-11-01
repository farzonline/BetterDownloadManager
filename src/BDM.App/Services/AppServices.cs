using BDM.App.Settings;
using BDM.App.ViewModels;
using BDM.Core.Data;
using BDM.Core.Engine;
using BDM.Core.Net;

namespace BDM.App.Services;

/// <summary>An entry in a "Route through" drop-down. Id is "" for "no proxy".</summary>
public sealed record ProxyChoice(string Id, string Name)
{
    public override string ToString() => Name;
}

/// <summary>HttpClients per proxy profile (null/unknown id = direct), cached until the profiles change.</summary>
public sealed class ProxyClientProvider(ProxyProfileStore profiles, CredentialVault vault)
{
    readonly Dictionary<string, HttpClient> _clients = new();
    readonly object _gate = new();
    HttpClient? _direct;

    /// <summary>The client that sends requests through the browser (Net.BrowserRelay); null while the relay isn't running.</summary>
    public HttpClient? Browser { get; set; }

    public HttpClient Get(string? profileId)
    {
        lock (_gate)
        {
            if (string.IsNullOrEmpty(profileId)) return _direct ??= HttpClients.Create(null, vault);
            if (profileId == BrowserRelay.ProfileId)
                return Browser ?? throw new InvalidOperationException("This download goes through your browser, but BDM's browser relay isn't running. Restart BDM and try again.");
            if (_clients.TryGetValue(profileId, out var c)) return c;
            var profile = profiles.Load().FirstOrDefault(p => p.Id == profileId);
            // A deleted profile must never quietly turn into a direct connection: that would expose the real address the profile was there to hide.
            if (profile == null) throw new InvalidOperationException("The proxy profile this download uses no longer exists. Pick another one, or none, and try again.");
            return _clients[profileId] = HttpClients.Create(profile, vault);
        }
    }

    /// <summary>Call after profiles or their passwords change. Running downloads keep the client they started with; the next one gets a fresh client.</summary>
    public void Invalidate()
    {
        lock (_gate) _clients.Clear(); // not disposed: a download may be in the middle of using one
    }

    public IReadOnlyList<ProxyChoice> Choices(string noneLabel = "No proxy") =>
        new[] { new ProxyChoice("", noneLabel) }
            .Concat(Browser != null ? [new ProxyChoice(BrowserRelay.ProfileId, "Through your browser (sites that block download managers)")] : [])
            .Concat(profiles.Load().Select(p => new ProxyChoice(p.Id, p.Name))).ToList();
}

/// <summary>Everything the secondary windows need, created once at startup.</summary>
public sealed class AppServices
{
    public required AppSettings Settings { get; init; }
    public required DownloadManager Manager { get; init; }
    public required DownloadStore Store { get; init; }
    public required ProxyProfileStore Profiles { get; init; }
    public required CredentialVault Vault { get; init; }
    public required SiteLogins Logins { get; init; }
    public required ProxyClientProvider Clients { get; init; }
    public required MainViewModel Main { get; init; }
    public required HostMessageHandler Handler { get; init; }
    /// <summary>Re-applies settings to the engine, theme and Windows startup entry (after Options or an import).</summary>
    public required Action ApplySettings { get; init; }
}
