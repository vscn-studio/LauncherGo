using System.Net;
using System.Text.Json.Serialization;

namespace ServerMap.Web;

public sealed record WebPageMetadata
{
    [JsonPropertyName("title")] public string Title { get; init; } = "ServerMap";
    [JsonPropertyName("description")] public string Description { get; init; } = "";
    [JsonPropertyName("keywords")] public string Keywords { get; init; } = "";
    [JsonPropertyName("faviconUrl")] public string FaviconUrl { get; init; } = "";

    public WebPageMetadata Normalize()
    {
        static string Clean(string? value, int limit)
        {
            value = (value ?? "").Trim();
            if (value.Length > limit) throw new ArgumentException("Website setting is too long.");
            return value;
        }
        var title = Clean(Title, 120);
        var favicon = Clean(FaviconUrl, 2048);
        if (favicon.Length > 0 && (!Uri.TryCreate(favicon, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https") || string.IsNullOrEmpty(uri.Host) || uri.UserInfo.Length > 0))
            throw new ArgumentException("Favicon must be an HTTP or HTTPS URL.");
        return this with { Title = title.Length == 0 ? "ServerMap" : title, Description = Clean(Description, 500), Keywords = Clean(Keywords, 500), FaviconUrl = favicon };
    }

    public string ApplyToHtml(string html)
    {
        const string start = "<!-- site-metadata:start -->", end = "<!-- site-metadata:end -->";
        var first = html.IndexOf(start, StringComparison.Ordinal);
        var last = html.IndexOf(end, StringComparison.Ordinal);
        if (first < 0 || last < first) return html; // Custom web roots may not use this template.
        var site = Normalize();
        static string E(string value) => WebUtility.HtmlEncode(value);
        var tags = $"<title>{E(site.Title)}</title>\n  <meta name=\"description\" content=\"{E(site.Description)}\">\n  <meta name=\"keywords\" content=\"{E(site.Keywords)}\">\n  <meta property=\"og:title\" content=\"{E(site.Title)}\">\n  <meta property=\"og:description\" content=\"{E(site.Description)}\">\n  <link id=\"siteFavicon\" rel=\"icon\" href=\"{E(site.FaviconUrl.Length == 0 ? "assets/icons/player.svg" : site.FaviconUrl)}\">";
        return html[..(first + start.Length)] + "\n  " + tags + "\n  " + html[last..];
    }
}
