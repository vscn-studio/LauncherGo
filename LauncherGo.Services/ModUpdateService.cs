using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using LauncherGo.Abstractions.Services;
using LauncherGo.Domains.Models;

namespace LauncherGo.Services;

/// <summary>
///     Reads release information from mods.vintagestory.at/api/mod/{modid}.
///     Releases are selected by semantic version; the API may return a newer
///     compatibility upload for an older game version after the newest release.
/// </summary>
public sealed class ModUpdateService : IModUpdateService
{
    private static readonly HttpClient SharedHttpClient = CreateHttpClient();

    public async Task<ModUpdateCheckResult> CheckAsync(
        ModEntry mod,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mod);
        if (string.IsNullOrWhiteSpace(mod.ModId))
            throw new InvalidOperationException("模组 ID 为空。");
        if (string.IsNullOrWhiteSpace(mod.Version) ||
            mod.Version.Equals("unknown", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("当前模组版本无效。");
        }

        var url = $"https://mods.vintagestory.at/api/mod/{Uri.EscapeDataString(mod.ModId.Trim())}";
        using var response = await SharedHttpClient.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = document.RootElement;
        if (!IsSuccessfulResponse(root) || !root.TryGetProperty("mod", out var modElement) ||
            modElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("模组库未返回有效数据。");
        }

        if (!modElement.TryGetProperty("releases", out var releasesElement) ||
            releasesElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("模组库未返回版本信息。");
        }

        var latestRelease = SelectLatestRelease(releasesElement.EnumerateArray());
        if (latestRelease.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            throw new InvalidOperationException("模组库未返回可用版本。");

        var latestVersion = GetString(latestRelease, "modversion");
        var homepage = BuildHomepage(modElement);
        var downloadUrl = GetString(latestRelease, "mainfile");
        var releaseDate = FormatDate(GetString(latestRelease, "created"));
        var changelog = NormalizeChangelog(GetString(latestRelease, "changelog"));

        return new ModUpdateCheckResult
        {
            ModId = mod.ModId,
            CurrentVersion = mod.Version,
            LatestVersion = latestVersion,
            IsUpdateAvailable = CompareVersions(mod.Version, latestVersion) < 0,
            ReleaseDate = releaseDate,
            Changelog = changelog,
            HomepageUrl = homepage,
            DownloadUrl = downloadUrl
        };
    }

    internal static int CompareVersions(string left, string right)
    {
        var leftParsed = ParseVersion(left);
        var rightParsed = ParseVersion(right);
        if (leftParsed is not null && rightParsed is not null)
            return CompareVersionParts(leftParsed.Value, rightParsed.Value);

        return string.Compare(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    internal static JsonElement SelectLatestRelease(IEnumerable<JsonElement> releases)
    {
        var versionComparer = Comparer<string>.Create(CompareVersions);
        return releases
            .Where(static item => item.ValueKind == JsonValueKind.Object &&
                                  GetString(item, "modversion").Length > 0)
            .OrderByDescending(static item => GetString(item, "modversion"), versionComparer)
            .ThenByDescending(static item => ParseDate(GetString(item, "created")))
            .FirstOrDefault();
    }

    private static bool IsSuccessfulResponse(JsonElement root)
    {
        if (!root.TryGetProperty("statuscode", out var status))
            return false;

        return (status.ValueKind == JsonValueKind.Number && status.TryGetInt32(out var code) && code == 200) ||
               (status.ValueKind == JsonValueKind.String && status.GetString()?.Trim() == "200");
    }

    private static string BuildHomepage(JsonElement mod)
    {
        var alias = GetString(mod, "urlalias");
        if (!string.IsNullOrWhiteSpace(alias))
            return $"https://mods.vintagestory.at/{alias.Trim().Trim('/')}";

        var homepage = GetString(mod, "homepageurl");
        if (!string.IsNullOrWhiteSpace(homepage))
            return homepage;

        if (mod.TryGetProperty("modid", out var modId) && modId.ValueKind == JsonValueKind.Number &&
            modId.TryGetInt32(out var numericId) && numericId > 0)
        {
            return $"https://mods.vintagestory.at/show/mod/{numericId}";
        }

        return string.Empty;
    }

    private static string GetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim() ?? string.Empty
            : string.Empty;
    }

    private static DateTimeOffset ParseDate(string value)
    {
        return DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed
            : DateTimeOffset.MinValue;
    }

    private static string FormatDate(string value)
    {
        var parsed = ParseDate(value);
        return parsed == DateTimeOffset.MinValue
            ? value
            : parsed.ToOffset(TimeSpan.FromHours(8)).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
    }

    private static string NormalizeChangelog(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var text = WebUtility.HtmlDecode(value);
        text = Regex.Replace(text, @"<\s*br\s*/?\s*>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<\s*/?\s*(?:li|p|div|h[1-6])\s*>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<[^>]+>", string.Empty);
        return text.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
    }

    private static (int[] Core, (int Kind, object Value)[]? PreRelease)? ParseVersion(string value)
    {
        value = value.Trim();
        if (value.StartsWith('v') || value.StartsWith('V'))
            value = value[1..];

        var match = Regex.Match(value, @"^(\d+(?:\.\d+)*)(?:-([0-9A-Za-z.-]+))?(?:\+.*)?$");
        if (!match.Success)
            return null;

        var core = match.Groups[1].Value.Split('.').Select(int.Parse).ToArray();
        var pre = match.Groups[2].Success
            ? match.Groups[2].Value.Split('.').Select(part =>
                int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
                    ? (0, (object)number)
                    : (1, (object)part.ToLowerInvariant())).ToArray()
            : null;
        return (core, pre);
    }

    private static int CompareVersionParts(
        (int[] Core, (int Kind, object Value)[]? PreRelease) left,
        (int[] Core, (int Kind, object Value)[]? PreRelease) right)
    {
        var length = Math.Max(left.Core.Length, right.Core.Length);
        for (var index = 0; index < length; index++)
        {
            var leftPart = index < left.Core.Length ? left.Core[index] : 0;
            var rightPart = index < right.Core.Length ? right.Core[index] : 0;
            if (leftPart != rightPart)
                return leftPart.CompareTo(rightPart);
        }

        if (left.PreRelease is null && right.PreRelease is null) return 0;
        if (left.PreRelease is null) return 1;
        if (right.PreRelease is null) return -1;

        for (var index = 0; index < Math.Min(left.PreRelease.Length, right.PreRelease.Length); index++)
        {
            var leftPart = left.PreRelease[index];
            var rightPart = right.PreRelease[index];
            if (leftPart.Kind != rightPart.Kind)
                return leftPart.Kind.CompareTo(rightPart.Kind);

            var comparison = leftPart.Kind == 0
                ? ((int)leftPart.Value).CompareTo((int)rightPart.Value)
                : string.Compare((string)leftPart.Value, (string)rightPart.Value, StringComparison.Ordinal);
            if (comparison != 0)
                return comparison;
        }

        return left.PreRelease.Length.CompareTo(right.PreRelease.Length);
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("LauncherGo/1.0");
        return client;
    }
}
