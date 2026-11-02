using BDM.App.Settings;
using BDM.Core.Http;

namespace BDM.App.Services;

/// <summary>What the "download file info" step settles before a download starts (IDM shows the same dialog).</summary>
/// <param name="FileName">The owner's own name; null = automatic (the server's name, else <paramref name="SuggestedName"/>).</param>
public sealed record DownloadPlan(Uri Url, string SuggestedName, string? FileName, string Folder, string Category,
    string? Description = null, bool StartNow = true, string? ProxyProfileId = null)
{
    /// <param name="title">The page's title; a video or audio caught on a page is named after it, as IDM does (CDN names are often just numbers).</param>
    /// <param name="kind">What the extension found: "video", "audio" or "file".</param>
    public static DownloadPlan Propose(Uri url, AppSettings settings, string? title = null, string? kind = null)
    {
        var name = Categories.NameFromUrl(url);
        string? titled = null;
        if (kind is "video" or "audio" && !string.IsNullOrWhiteSpace(title))
        {
            var ext = Categories.For(name) is "Video" or "Music" ? Path.GetExtension(name) : kind == "audio" ? ".m4a" : ".mp4";
            titled = Core.Files.FileNaming.Sanitize(title.Trim() + ext);
        }
        var category = Categories.For(titled ?? name);
        return new DownloadPlan(url, name, titled, Categories.FolderFor(settings, category), category);
    }

    /// <summary>A web or FTP address in copied text, or null.</summary>
    public static Uri? LinkFrom(string? text) =>
        Uri.TryCreate(text?.Trim(), UriKind.Absolute, out var u) && u.Scheme is "http" or "https" or "ftp" or "ftps" ? u : null;

    /// <summary>The request to queue: the template's headers and cookies, with the owner's choices on top.</summary>
    public DownloadRequest ApplyTo(DownloadRequest template) => template with
    {
        Url = Url,
        FileName = string.IsNullOrWhiteSpace(FileName) ? null : FileName.Trim(),
        DestinationDir = Folder,
        Category = Category,
        Description = string.IsNullOrWhiteSpace(Description) ? null : Description.Trim(),
        ProxyProfileId = string.IsNullOrWhiteSpace(ProxyProfileId) ? template.ProxyProfileId : ProxyProfileId,
    };
}

/// <summary>Shows the download dialog (requests from the browser arrive on pipe threads). Null = cancelled.</summary>
public interface IDownloadPrompt
{
    /// <param name="template">The request as it would be sent, so the dialog can look the file up with the browser's headers.</param>
    Task<DownloadPlan?> AskAsync(DownloadPlan proposal, DownloadRequest template);
}
