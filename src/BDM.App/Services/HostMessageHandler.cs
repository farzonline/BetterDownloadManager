using BDM.App.Settings;
using BDM.Core.Engine;
using BDM.Core.Http;
using BDM.Core.Ipc;
using BDM.Core.Streams;

namespace BDM.App.Services;

/// <summary>Shows the owner the qualities of a stream and returns their pick (null = cancelled).</summary>
public interface IQualityPicker
{
    Task<StreamChoice?> PickAsync(IReadOnlyList<StreamChoice> choices, string title);
}

/// <summary>Turns messages from the browser extension (and second launches) into downloads.</summary>
public sealed class HostMessageHandler(DownloadManager manager, AppSettings settings, IQualityPicker picker)
{
    public AppSettings Settings => settings;
    public event Action? Activated;

    static bool IsDownloadable(Uri u) => u.Scheme is "http" or "https" or "ftp" or "ftps";

    public async Task<HostReply> HandleAsync(HostMessage msg)
    {
        switch (msg.Op)
        {
            case "ping": return Ok();
            case "activate": Activated?.Invoke(); return Ok();
            case "add": return Add(msg);
            case "addStream":
            case "addStreamAsk": return await AddStreamAsync(msg, ask: msg.Op == "addStreamAsk" || settings.AlwaysAskQuality);
            default: return Fail($"Unknown request '{msg.Op}'.");
        }
    }

    static HostReply Ok() => new(true, null, false);
    static HostReply Fail(string error) => new(false, error, false);

    HostReply Add(HostMessage msg)
    {
        if (!Uri.TryCreate(msg.Url, UriKind.Absolute, out var url) || !IsDownloadable(url)) return Fail("That is not a downloadable address.");
        var name = Categories.NameFromUrl(url);
        var category = Categories.For(name);
        var headers = (msg.Headers ?? new()).Where(h => !h.Key.Equals("Referer", StringComparison.OrdinalIgnoreCase) && !h.Key.Equals("Cookie", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(h => h.Key, h => h.Value);
        var referer = msg.Headers?.FirstOrDefault(h => h.Key.Equals("Referer", StringComparison.OrdinalIgnoreCase)).Value ?? msg.PageUrl;
        manager.Add(new DownloadRequest(url, null, referer, headers, msg.Cookie, null, category, Categories.FolderFor(settings, category)));
        return Ok();
    }

    async Task<HostReply> AddStreamAsync(HostMessage msg, bool ask)
    {
        if (!Uri.TryCreate(msg.Url, UriKind.Absolute, out var url) || !IsDownloadable(url)) return Fail("That is not a downloadable address.");
        var streams = manager.Streams;
        if (streams == null) return Fail("Stream downloads are not set up. Check the ffmpeg path in Options.");

        var kind = string.Equals(msg.Kind, "dash", StringComparison.OrdinalIgnoreCase) || url.AbsolutePath.EndsWith(".mpd", StringComparison.OrdinalIgnoreCase)
            ? StreamKind.Dash : StreamKind.Hls;
        var source = new StreamSource(url, kind, msg.Headers ?? new(), msg.Cookie, msg.Title, msg.ManifestBody);

        IReadOnlyList<StreamChoice> choices;
        try { choices = await streams.ListChoicesAsync(source, default); }
        catch (ProtectedContentException) { return Fail("This stream is protected (DRM) and cannot be downloaded."); }
        catch (Exception e) when (e is DownloadException or HttpRequestException or FormatException or NotSupportedException)
        {
            return Fail(e.Message);
        }
        if (choices.Count == 0) return Fail("No downloadable quality was found in this stream.");

        var choice = choices[0]; // best first
        if (ask && choices.Count > 1)
        {
            var picked = await picker.PickAsync(choices, msg.Title ?? Categories.NameFromUrl(url));
            if (picked == null) return Fail("Cancelled: no quality was chosen.");
            choice = picked;
        }
        manager.AddStream(source, choice, Categories.FolderFor(settings, "Video"));
        return Ok();
    }
}
