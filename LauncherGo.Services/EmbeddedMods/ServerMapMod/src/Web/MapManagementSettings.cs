namespace ServerMap.Web;

public sealed record MapManagementSettings
{
    public sealed record LayerRule(bool DefaultVisible = false, bool Forbidden = false, bool Forced = false, double Scale = 1);
    public static readonly string[] LayerIds = ["players", "mounts", "spawn", "claims", "claim-areas", "chunks", "translocators", "pois"];
    public string[] ImageTypes { get; init; } = ["jpeg", "png", "webp", "bmp"];
    public int ImageMaxMb { get; init; } = 10;
    public int PoiQuota { get; init; } = 10;
    public int DailyTeleports { get; init; } = 0;
    public Dictionary<string, LayerRule> Layers { get; init; } = new();
    public static bool SupportsScale(string id) => id is "players" or "mounts" or "translocators";
    public static string NormalizeImageType(string type) => type.Trim().TrimStart('.').ToLowerInvariant() switch { "jpg" => "jpeg", "apng" => "png", var value => value };
    public LayerRule Layer(string id)
    {
        var rule = Layers.GetValueOrDefault(id) ?? new(id is "players" or "mounts" or "spawn" or "pois");
        return SupportsScale(id) ? rule : rule with { Scale = 1 };
    }
    public MapManagementSettings Validate()
    {
        if (ImageTypes == null || ImageTypes.Length == 0 || ImageTypes.Length > 32 || ImageTypes.Any(t => t == null || !System.Text.RegularExpressions.Regex.IsMatch(NormalizeImageType(t), "^[a-z][a-z0-9]{0,15}$"))
            || ImageMaxMb is < 1 or > 50 || PoiQuota is < 0 or > 10000 || DailyTeleports is < 0 or > 100000 || Layers == null
            || Layers.Any(p => !LayerIds.Contains(p.Key) || p.Value == null || p.Value.Forbidden && p.Value.Forced
                || SupportsScale(p.Key) && (!double.IsFinite(p.Value.Scale) || p.Value.Scale is < .1 or > 10)))
            throw new ArgumentException("Invalid map management settings");
        return this with { ImageTypes = ImageTypes.Select(NormalizeImageType).Distinct().ToArray(), Layers = Layers.ToDictionary(p => p.Key, p => Layer(p.Key)) };
    }
}
