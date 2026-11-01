using BDM.App.Settings;
using BDM.Core.Engine;
using BDM.Core.Ext;
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
/// <param name="prompt">The download dialog shown before a file from the browser starts; null = never ask.</param>
/// <param name="probe">Looks a file from the browser up first, to learn whether the site lets BDM fetch it; null = don't check.</param>
public sealed class HostMessageHandler(DownloadManager manager, AppSettings settings, IQualityPicker picker, IDownloadPrompt? prompt = null,
    Func<DownloadRequest, Task<FileProbe>>? probe = null)
{
    public AppSettings Settings => settings;
    public event Action? Activated;
    /// <summary>A file download the owner started from the download dialog (the app opens its progress window).</summary>
    public event Action<Guid>? Started;

    static bool IsDownloadable(Uri u) => u.Scheme is "http" or "https" or "ftp" or "ftps";

    public async Task<HostReply> HandleAsync(HostMessage msg)
    {
        switch (msg.Op)
        {
            case "ping": return Ok();
            case "activate": Activated?.Invoke(); return Ok();
            case "add": return await AddAsync(msg);
            case "addStream":
            case "addStreamAsk": return await AddStreamAsync(msg, ask: msg.Op == "addStreamAsk" || settings.AlwaysAskQuality);
            case "addPage":
            case "addPageAsk": return await AddPageAsync(msg, ask: msg.Op == "addPageAsk" || settings.AlwaysAskQuality);
            default: return Fail($"Unknown request '{msg.Op}'.");
        }
    }

    static HostReply Ok() => new(true, null, false);
    static HostReply Fail(string error) => new(false, error, false);

    async Task<HostReply> AddAsync(HostMessage msg)
    {
        if (!Uri.TryCreate(msg.Url, UriKind.Absolute, out var url) || !IsDownloadable(url)) return Fail("That is not a downloadable address.");
        var plan = DownloadPlan.Propose(url, settings);
        var headers = (msg.Headers ?? new()).Where(h => !h.Key.Equals("Referer", StringComparison.OrdinalIgnoreCase) && !h.Key.Equals("Cookie", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(h => h.Key, h => h.Value);
        var referer = msg.Headers?.FirstOrDefault(h => h.Key.Equals("Referer", StringComparison.OrdinalIgnoreCase)).Value ?? msg.PageUrl;
        var template = new DownloadRequest(url, null, referer, headers, msg.Cookie, null, plan.Category, plan.Folder);
        if (await RefusesBdmAsync(template))
            return new HostReply(false, "This site only lets your browser download the file, so the browser is downloading it.", false, UseBrowser: true);
        if (prompt != null && settings.ShowDownloadDialog)
        {
            // Cancelled still answers Ok: the extension then drops the browser's own copy too, as the owner asked.
            if (await prompt.AskAsync(plan, template) is not { } chosen) return Ok();
            plan = chosen;
        }
        var id = manager.Add(plan.ApplyTo(template));
        if (!plan.StartNow) manager.Pause(id);
        else if (prompt != null && settings.ShowDownloadDialog) Started?.Invoke(id);
        return Ok();
    }

    /// <summary>
    /// Bot protection (Cloudflare and the like) refuses anything that isn't a real browser, even with the browser's headers:
    /// 401/403/429/503 here means hand the file back. Anything else (timeouts, network) is left to BDM's own retries.
    /// </summary>
    async Task<bool> RefusesBdmAsync(DownloadRequest template)
    {
        if (probe == null) return false;
        try { await probe(template); return false; }
        catch (DownloadException e) when (e.Kind == DownloadFailure.AuthExpired) { return true; }
        catch (HttpRequestException e) when ((int?)e.StatusCode is 401 or 403 or 429 or 503) { return true; }
        catch (Exception e) when (e is HttpRequestException or DownloadException or OperationCanceledException or IOException) { return false; }
    }

    async Task<HostReply> AddStreamAsync(HostMessage msg, bool ask)
    {
        if (!Uri.TryCreate(msg.Url, UriKind.Absolute, out var url) || !IsDownloadable(url)) return Fail("That is not a downloadable address.");
        var streams = manager.StreamDownloaderFor(manager.ProxyIdFor("Video", "main"));
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

    /// <summary>A video page (not a direct file or manifest): let yt-dlp work out what to download.</summary>
    async Task<HostReply> AddPageAsync(HostMessage msg, bool ask)
    {
        if (!Uri.TryCreate(msg.Url, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https")) return Fail("That is not a web page address.");
        if (manager.YtDlp is not { } yt) return Fail("yt-dlp isn't set up. Download yt-dlp.exe and set its path in Options > Tools.");

        var headers = new Dictionary<string, string>(msg.Headers ?? new());
        if (!string.IsNullOrEmpty(msg.Cookie)) headers["Cookie"] = msg.Cookie;
        YtDlpInfo info;
        try
        {
            var proxyUrl = manager.ProxyUrlFor?.Invoke(manager.ProxyIdFor("Video", "main"));
            info = await yt.InfoAsync(url, headers, default, proxyUrl);
        }
        catch (Exception e) when (e is YtDlpException or YtDlpMissingException or InvalidOperationException) { return Fail(e.Message); }

        var choice = info.Choices[0]; // best first
        if (ask && info.Choices.Count > 1)
        {
            var picked = await picker.PickAsync(info.Choices, string.IsNullOrWhiteSpace(info.Title) ? msg.Title ?? url.Host : info.Title);
            if (picked == null) return Fail("Cancelled: no quality was chosen.");
            choice = picked;
        }
        manager.AddYtDlp(url, choice, Categories.FolderFor(settings, "Video"), msg.Headers, msg.Cookie);
        return Ok();
    }
}
