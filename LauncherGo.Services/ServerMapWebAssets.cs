using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace LauncherGo.Services;

public static partial class ServerMapWebAssets
{
    // Only version local script/link attributes; never rewrite custom inline code or remote URLs.
    [GeneratedRegex("<!--[\\s\\S]*?-->|<script\\b[^>]*>[\\s\\S]*?</script\\s*>|<link\\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex AssetElements();

    [GeneratedRegex("(\\s(?:src|href)\\s*=\\s*)([\"'])([^\"']+)([\"'])", RegexOptions.IgnoreCase)]
    private static partial Regex AssetReferences();

    public static string VersionHtml(string html, string webRoot)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(webRoot)) + Path.DirectorySeparatorChar;
        return AssetElements().Replace(html, element =>
        {
            if (element.Value.StartsWith("<!--", StringComparison.Ordinal)) return element.Value;
            var end = element.Value.IndexOf('>') + 1;
            return AssetReferences().Replace(element.Value[..end], VersionReference) + element.Value[end..];
        });

        string VersionReference(Match match)
        {
            var url = WebUtility.HtmlDecode(match.Groups[3].Value);
            if (url.StartsWith('/') || url.StartsWith('#') || Uri.TryCreate(url, UriKind.Absolute, out _)) return match.Value;
            var fragmentIndex = url.IndexOf('#');
            var fragment = fragmentIndex < 0 ? "" : url[fragmentIndex..];
            if (fragmentIndex >= 0) url = url[..fragmentIndex];
            var parts = url.Split('?', 2);
            var path = Path.GetFullPath(Path.Combine(root, Uri.UnescapeDataString(parts[0])));
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(path)) return match.Value;
            using var input = File.OpenRead(path);
            var version = Convert.ToHexString(SHA256.HashData(input))[..16];
            var query = parts.Length > 1 ? parts[1].Split('&').Where(p => p.Length > 0 && !p.StartsWith("lgweb=", StringComparison.Ordinal)).ToList() : [];
            query.Add("lgweb=" + version);
            var versioned = parts[0] + "?" + string.Join('&', query) + fragment;
            return match.Groups[1].Value + match.Groups[2].Value + WebUtility.HtmlEncode(versioned) + match.Groups[4].Value;
        }
    }
}
