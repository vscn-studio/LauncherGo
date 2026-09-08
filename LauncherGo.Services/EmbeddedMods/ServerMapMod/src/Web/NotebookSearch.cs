namespace ServerMap.Web;

/// <summary>Search only data owned by the caller, with the same fog rules as map display.</summary>
public static class NotebookSearch
{
    public sealed record Result(string kind, string id, string name, double x, double z, bool hasLocation = true);

    public static IEnumerable<Result> Find(string query, string? owner, bool admin,
        GameWaypointSnapshot waypoints, MapNotebookStore notebook)
    {
        if (owner == null || string.IsNullOrWhiteSpace(query)) yield break;
        query = query.Trim();
        bool Match(string? value) => value?.Contains(query, StringComparison.OrdinalIgnoreCase) == true;
        var regions = notebook.Regions;
        foreach (var marker in waypoints.ForOwner(owner))
            if ((Match(marker.Name) || Match(marker.Text) || Match(marker.Icon)) && (admin || MapVisibility.Visible(regions, marker.X, marker.Z)))
                yield return new("waypoint", marker.Id, marker.Name, marker.X, marker.Z);
        foreach (var route in notebook.ForOwner(owner))
            if (Match(route.Name) && (admin || MapVisibility.RouteVisible(regions, route.Points)))
                yield return new("route", route.Id, route.Name, route.Points[0][0], route.Points[0][1]);
        if (admin)
            foreach (var region in regions)
                if (Match(region.Name)) yield return new("hidden-region", region.Id, region.Name, (region.MinX + region.MaxX) / 2, (region.MinZ + region.MaxZ) / 2);
    }
}
