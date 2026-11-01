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

    public HttpClient Get(string? profileId)
    {
        lock (_gate)
        {
            if (string.IsNullOrEmpty(profileId)) return _direct ??= HttpClients.Create(null, vault);
            if (_clients.TryGetValue(profileId, out var c)) return c;
            var profile = profiles.Load().FirstOrDefault(p => p.Id == profileId);
            if (profile == null) return _direct ??= HttpClients.Create(null, vault); // profile was deleted: fall back to direct
            return _clients[profileId] = HttpClients.Create(profile, vault);
        }
    }

    /// <summary>Call after profiles or their passwords change.</summary>
    public void Invalidate()
    {
        lock (_gate)
        {
            foreach (var c in _clients.Values) c.Dispose();
            _clients.Clear();
        }
    }

    public IReadOnlyList<ProxyChoice> Choices(string noneLabel = "No proxy") =>
        new[] { new ProxyChoice("", noneLabel) }.Concat(profiles.Load().Select(p => new ProxyChoice(p.Id, p.Name))).ToList();
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
    /// <summary>Re-applies settings to the engine, theme and Windows startup entry (after Options or an import).</summary>
    public required Action ApplySettings { get; init; }
}
