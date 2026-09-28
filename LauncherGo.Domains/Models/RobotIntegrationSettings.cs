namespace LauncherGo.Domains.Models;

public sealed class RobotIntegrationSettings
{
    public string OneBotWsUrl { get; set; } = "ws://127.0.0.1:3001/";

    public string AccessToken { get; set; } = string.Empty;

    public string BoundGroupIdsText { get; set; } = string.Empty;

    public int ReconnectIntervalSec { get; set; } = 5;

    public string DatabasePath { get; set; } = string.Empty;

    public string DefaultEncoding { get; set; } = "utf-8";

    public string FallbackEncoding { get; set; } = "gbk";

    public string SuperUsersText { get; set; } = string.Empty;

    public List<RobotProfileBinding> ProfileBindings { get; set; } = [];

    public List<RobotCustomCommand> CustomCommands { get; set; } = [];

    public List<RobotTeleportPoint> TeleportPoints { get; set; } = [];

    public bool RelayChatMessages { get; set; } = true;

    public bool RelayPlayerEvents { get; set; } = true;

    public bool RelayServerNotifications { get; set; } = true;

    public bool EnablePlayerBinding { get; set; } = true;

}

public sealed class RobotProfileBinding
{
    public string ProfileId { get; set; } = string.Empty;

    public string GroupId { get; set; } = string.Empty;

    public string SuperUserId { get; set; } = string.Empty;
}

public sealed class RobotTeleportPoint
{
    public string Name { get; set; } = string.Empty;

    public double X { get; set; }

    public double Y { get; set; }

    public double Z { get; set; }
}

public static class RobotTeleportPointRules
{
    public const int MaxNameLength = 64;
    public const double MaxCoordinateMagnitude = 1_000_000_000d;

    public static bool TryNormalize(RobotTeleportPoint? source, out RobotTeleportPoint normalized)
    {
        normalized = new RobotTeleportPoint();
        var name = source?.Name?.Trim() ?? string.Empty;
        if (source is null ||
            string.IsNullOrWhiteSpace(name) ||
            name.Length > MaxNameLength ||
            name.Any(char.IsControl) ||
            !double.IsFinite(source.X) ||
            !double.IsFinite(source.Y) ||
            !double.IsFinite(source.Z) ||
            Math.Abs(source.X) > MaxCoordinateMagnitude ||
            Math.Abs(source.Y) > MaxCoordinateMagnitude ||
            Math.Abs(source.Z) > MaxCoordinateMagnitude)
        {
            return false;
        }

        normalized = new RobotTeleportPoint
        {
            Name = name,
            X = source.X,
            Y = source.Y,
            Z = source.Z
        };
        return true;
    }

    public static List<RobotTeleportPoint> NormalizeMany(IEnumerable<RobotTeleportPoint>? points)
    {
        var result = new List<RobotTeleportPoint>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var point in points ?? [])
        {
            if (TryNormalize(point, out var normalized) && names.Add(normalized.Name))
                result.Add(normalized);
        }
        return result;
    }
}
