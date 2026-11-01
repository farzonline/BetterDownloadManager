namespace BDM.Core.Net;

public enum ProxyKind { Http, Socks5 }

/// <summary>A named proxy (for example a Surfshark manual-connection server). The password lives in <see cref="CredentialVault"/>.</summary>
public record ProxyProfile(string Id, string Name, ProxyKind Kind, string Host, int Port, string? Username)
{
    public string VaultKey => "proxy:" + Id;
}
