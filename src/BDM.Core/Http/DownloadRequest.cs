namespace BDM.Core.Http;

public record DownloadRequest(
    Uri Url,
    string? FileName,
    string? Referer,
    IReadOnlyDictionary<string, string> Headers,
    string? CookieHeader,
    string? ProxyProfileId,
    string? Category,
    string DestinationDir)
{
    /// <summary>The owner's own note, shown in the list.</summary>
    public string? Description { get; init; }

    /// <summary>Builds a request message carrying this download's Referer, Cookie and custom headers.</summary>
    public HttpRequestMessage CreateMessage(HttpMethod method)
    {
        var m = new HttpRequestMessage(method, Url);
        if (Referer != null) m.Headers.TryAddWithoutValidation("Referer", Referer);
        if (CookieHeader != null) m.Headers.TryAddWithoutValidation("Cookie", CookieHeader);
        foreach (var (k, v) in Headers) m.Headers.TryAddWithoutValidation(k, v);
        return m;
    }
}
