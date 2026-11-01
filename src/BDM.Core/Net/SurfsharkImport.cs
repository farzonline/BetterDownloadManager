namespace BDM.Core.Net;

public static class SurfsharkImport
{
    /// <summary>
    /// Parses a pasted server list: one per line, `host:port` or `name,host,port`; blank lines and `#` comments are skipped.
    /// [call] Port 1080 is taken as SOCKS5, anything else as an HTTP(S) proxy. Usernames are set afterwards (service credentials).
    /// </summary>
    public static IReadOnlyList<ProxyProfile> Parse(string text)
    {
        var list = new List<ProxyProfile>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            string name, host;
            int port;
            if (line.Contains(','))
            {
                var p = line.Split(',', StringSplitOptions.TrimEntries);
                if (p.Length < 3 || !int.TryParse(p[2], out port)) continue;
                (name, host) = (p[0], p[1]);
            }
            else
            {
                var i = line.LastIndexOf(':');
                if (i < 1 || !int.TryParse(line[(i + 1)..], out port)) continue;
                host = line[..i];
                name = host;
            }
            list.Add(new ProxyProfile($"surfshark:{host}:{port}", name, port == 1080 ? ProxyKind.Socks5 : ProxyKind.Http, host, port, null));
        }
        return list;
    }
}
