namespace ServerMap.Web;

/// <summary>Destination permission, independent of map layer/preview preferences.</summary>
public static class TeleportAccess
{
    public const string HiddenRegion = "Hidden region"; // Preserve the existing API error.
    public const string Unexplored = "teleport_unexplored";

    public static string? Denial(MapManagementSettings settings, bool admin, bool hidden, Func<bool> explored)
    {
        // Turning off exploration fog must never turn off hidden-region privacy.
        if (!admin && hidden) return HiddenRegion;
        if (!settings.FogEnabled || admin && settings.AdminsBypassFog) return null;
        return explored() ? null : Unexplored;
    }
}
