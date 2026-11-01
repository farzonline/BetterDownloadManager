using System.Text;
using System.Text.Json;

namespace BDM.Core.Net;

public record SiteLogin(string Host, string Username);

/// <summary>
/// Per-site HTTP Basic credentials. Usernames live in a JSON file, passwords only in the DPAPI <see cref="CredentialVault"/>.
/// A login for example.com also applies to its subdomains; the most specific host wins.
/// </summary>
public sealed class SiteLogins
{
    readonly string _path;
    readonly CredentialVault _vault;
    readonly object _gate = new();
    List<SiteLogin> _logins;

    public SiteLogins(string path, CredentialVault vault)
    {
        _path = path;
        _vault = vault;
        _logins = Read();
    }

    static string Key(string host) => "site:" + host;

    List<SiteLogin> Read()
    {
        try { return File.Exists(_path) ? JsonSerializer.Deserialize<List<SiteLogin>>(File.ReadAllText(_path)) ?? new() : new(); }
        catch (Exception e) when (e is JsonException or IOException) { return new(); }
    }

    public IReadOnlyList<SiteLogin> All() { lock (_gate) return _logins.ToList(); }

    public void Set(string host, string username, string password)
    {
        host = host.Trim().ToLowerInvariant();
        lock (_gate)
        {
            _logins.RemoveAll(l => l.Host == host);
            _logins.Add(new SiteLogin(host, username));
            _vault.Save(Key(host), password);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
            File.WriteAllText(_path, JsonSerializer.Serialize(_logins));
        }
    }

    public void Remove(string host)
    {
        host = host.Trim().ToLowerInvariant();
        lock (_gate)
        {
            if (_logins.RemoveAll(l => l.Host == host) == 0) return;
            File.WriteAllText(_path, JsonSerializer.Serialize(_logins));
            _vault.Remove(Key(host)); // never leave a password behind for a login that no longer exists
        }
    }

    /// <summary>"Basic …" header value for the URL's host, or null when no login matches (or its password is missing).</summary>
    public string? AuthorizationFor(Uri url)
    {
        SiteLogin? match;
        lock (_gate)
        {
            match = _logins
                .Where(l => url.Host.Equals(l.Host, StringComparison.OrdinalIgnoreCase) || url.Host.EndsWith("." + l.Host, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(l => l.Host.Length)
                .FirstOrDefault();
        }
        if (match == null || _vault.Load(Key(match.Host)) is not { } password) return null;
        return "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{match.Username}:{password}"));
    }
}
