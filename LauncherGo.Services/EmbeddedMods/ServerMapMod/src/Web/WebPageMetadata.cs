using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ServerMap.Web;

public sealed record WebPageMetadata
{
    [JsonPropertyName("title")] public string Title { get; init; } = "ServerMap";
    [JsonPropertyName("description")] public string Description { get; init; } = "";
    [JsonPropertyName("keywords")] public string Keywords { get; init; } = "";
    [JsonPropertyName("faviconUrl")] public string FaviconUrl { get; init; } = "";
    [JsonPropertyName("customCss")] public string CustomCss { get; init; } = "";
    [JsonPropertyName("customJs")] public string CustomJs { get; init; } = "";

    public WebPageMetadata Normalize()
    {
        static string Clean(string? value, int limit)
        {
            value = (value ?? "").Trim();
            if (value.Length > limit) throw new ArgumentException("Website setting is too long.");
            return value;
        }
        static string CleanCode(string? value, int limit, string name)
        {
            value ??= "";
            if (value.Length > limit) throw new ArgumentException($"{name} is too long.");
            return value;
        }
        var title = Clean(Title, 120);
        var favicon = Clean(FaviconUrl, 2048);
        if (favicon.Length > 0 && (!Uri.TryCreate(favicon, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https") || string.IsNullOrEmpty(uri.Host) || uri.UserInfo.Length > 0))
            throw new ArgumentException("Favicon must be an HTTP or HTTPS URL.");
        return this with { Title = title.Length == 0 ? "ServerMap" : title, Description = Clean(Description, 500), Keywords = Clean(Keywords, 500), FaviconUrl = favicon,
            CustomCss = CleanCode(CustomCss, 100_000, "Custom CSS"), CustomJs = CleanCode(CustomJs, 100_000, "Custom JS") };
    }

    public string ApplyToHtml(string html)
    {
        const string start = "<!-- site-metadata:start -->", end = "<!-- site-metadata:end -->";
        const string cssStart = "<!-- site-custom-css:start -->", cssEnd = "<!-- site-custom-css:end -->", jsStart = "<!-- site-custom-js:start -->", jsEnd = "<!-- site-custom-js:end -->";
        html = RemoveBlock(RemoveBlock(html, cssStart, cssEnd), jsStart, jsEnd);
        var first = html.IndexOf(start, StringComparison.Ordinal);
        var last = html.IndexOf(end, StringComparison.Ordinal);
        if (first < 0 || last < first) return html; // Custom web roots may not use this template.
        var site = Normalize();
        static string E(string value) => WebUtility.HtmlEncode(value);
        var tags = $"<title>{E(site.Title)}</title>\n  <meta name=\"description\" content=\"{E(site.Description)}\">\n  <meta name=\"keywords\" content=\"{E(site.Keywords)}\">\n  <meta property=\"og:title\" content=\"{E(site.Title)}\">\n  <meta property=\"og:description\" content=\"{E(site.Description)}\">\n  <link id=\"siteFavicon\" rel=\"icon\" href=\"{E(site.FaviconUrl.Length == 0 ? "assets/icons/player.svg" : site.FaviconUrl)}\">";
        var result = html[..(first + start.Length)] + "\n  " + tags + "\n  " + html[last..];
        if (site.CustomCss.Length > 0)
        {
            var css = $"{cssStart}<style id=\"siteCustomCss\">{site.CustomCss.Replace("</style", "<\\/style", StringComparison.OrdinalIgnoreCase)}</style>{cssEnd}";
            var headEnd = result.IndexOf("</head>", StringComparison.OrdinalIgnoreCase);
            result = headEnd < 0 ? result : result.Insert(headEnd, css);
        }
        if (site.CustomJs.Length > 0)
        {
            // JSON's default encoder protects HTML raw-text boundaries without changing
            // the supplied JavaScript. A DOM-created script retains normal global scope.
            var source = JsonSerializer.Serialize(site.CustomJs);
            var script = $"{jsStart}<script id=\"siteCustomJs\">(()=>{{const anchor=document.currentScript,run=()=>{{const script=document.createElement('script');script.id='siteCustomJs';script.textContent={source};anchor.replaceWith(script);}};if(document.readyState==='loading')document.addEventListener('DOMContentLoaded',run,{{once:true}});else run();}})();</script>{jsEnd}";
            var bodyEnd = result.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
            result = bodyEnd < 0 ? result + script : result.Insert(bodyEnd, script);
        }
        return result;
    }

    private static string RemoveBlock(string html, string start, string end)
    {
        while (true)
        {
            var first = html.IndexOf(start, StringComparison.Ordinal);
            if (first < 0) return html;
            var last = html.IndexOf(end, first + start.Length, StringComparison.Ordinal);
            if (last < 0) return html;
            html = html.Remove(first, last + end.Length - first);
        }
    }
}
