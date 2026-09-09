using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace ServerMap.Web;

public sealed partial class ServerMapWebServer
{
    private readonly MapNotebookStore notebook;
    private readonly GameWaypointSnapshot waypoints = new();
    private WaypointIconStore waypointIcons = null!;
    private long waypointListener;
    private WaypointShareStore waypointShares = null!;
    public Func<object>? RenderProgress { get; set; }
    public MapNotebookStore.Region[] HiddenRegions => notebook.Regions;

    private void InitializeNotebook()
    {
        waypointIcons = new WaypointIconStore(Path.Combine(root, "waypoint-icons"));
        waypointShares = new WaypointShareStore(Path.Combine(root, "waypoint-shares.json"));
        // Same asset lookup and icon naming as Vintage Story's WaypointMapLayer.
        foreach (var asset in api.Assets.GetMany("textures/icons/worldmap/"))
        {
            if (!asset.Name.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)) continue;
            var name = Regex.Replace(Path.GetFileNameWithoutExtension(asset.Name), "\\d+\\-", "");
            try { waypointIcons.Put(name, asset.Data); }
            catch (Exception ex) { api.Logger.Warning("ServerMap skipped waypoint icon {0}: {1}", name, ex.Message); }
        }
        waypointListener = api.Event.RegisterGameTickListener(_ => CaptureWaypoints(), 5000);
    }
    public void ReceiveWaypointIcon(Vintagestory.API.Server.IServerPlayer player, ServerMap.Network.ClientWaypointIconPacket packet)
    {
        if (!player.HasPrivilege("root") || packet.Data is not { Length: > 0 and <= WaypointIconStore.MaxBytes } || string.IsNullOrEmpty(packet.Name)) return;
        try { waypointIcons.Put(packet.Name, packet.Data); }
        catch (Exception ex) { api.Logger.Warning("ServerMap rejected waypoint icon: {0}", ex.Message); }
    }
    private void CaptureWaypoints()
    {
        try
        {
            var layer = api.ModLoader.GetModSystem<WorldMapManager>()?.MapLayers.OfType<WaypointMapLayer>().FirstOrDefault();
            if (layer == null) return;
            var snapshot = layer.Waypoints.Where(w => w.Position != null).Select(SnapshotWaypoint).ToArray();
            waypoints.Replace(snapshot);
            waypointShares.Prune(snapshot);
        }
        catch (Exception ex) { api.Logger.Warning("ServerMap waypoint snapshot failed: {0}", ex.Message); }
    }
    private bool CanView(MapAuthStore.Principal? principal, double x, double z) => principal?.IsAdmin == true || MapVisibility.Visible(notebook.Regions, x, z);
    private bool PoiVisible(MapAuthStore.Principal? principal, PoiStore.Poi poi) => principal?.IsAdmin == true || !notebook.Regions.Any(r => MapVisibility.Intersects(r,
        Math.Min(poi.X, poi.X2 ?? poi.X), Math.Min(poi.Z, poi.Z2 ?? poi.Z), Math.Max(poi.X, poi.X2 ?? poi.X), Math.Max(poi.Z, poi.Z2 ?? poi.Z)));
    private object[] VisibleFeatures(List<object> features, MapAuthStore.Principal? principal)
    {
        if (principal?.IsAdmin == true) return features.ToArray();
        var regions = notebook.Regions;
        if (regions.Length == 0) return features.ToArray();
        return features.Where(f => MapVisibility.FeatureVisible(regions, JsonSerializer.SerializeToElement(f))).ToArray();
    }
    private static object RouteView(MapNotebookStore.Route route) => new { id = route.Id, name = route.Name, color = route.Color, points = route.Points, updatedAt = route.UpdatedAt };
    private bool RouteAllowed(MapAuthStore.Principal? principal, MapNotebookStore.Route route) => principal?.IsAdmin == true || MapVisibility.RouteVisible(notebook.Regions, route.Points);
    private bool NotebookRequest(HttpListenerContext context, string path)
    {
        if (WaypointRequest(context, path)) return true;
        if (path.StartsWith("api/v1/waypoint-icons/", StringComparison.Ordinal))
        {
            var name = Uri.UnescapeDataString(path[22..]);
            var svg = waypointIcons.Get(name);
            if (svg == null) { NotFound(context); return true; }
            context.Response.Headers["Content-Security-Policy"] = "default-src 'none'; sandbox";
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            ServeBytes(context, svg, "image/svg+xml", "no-store"); return true;
        }
        if (path == "api/v1/render-progress") { Json(context, RenderProgress?.Invoke() ?? new { phase = "starting", queued = 0 }, true); return true; }
        if (path != "api/v1/my-waypoints" && path != "api/v1/routes" && path != "api/v1/route-shares" && path != "api/v1/hidden-regions") return false;
        var principal = Principal(context.Request); var method = context.Request.HttpMethod;
        if (path == "api/v1/my-waypoints")
        {
            if (method != "GET") { Error(context, 405, "Method not allowed"); return true; }
            if (principal == null) { Error(context, 401, "Login required"); return true; }
            Json(context, waypoints.ForOwner(principal.PlayerUid).Where(w => CanView(principal, w.X, w.Z)).Select(w => new
            {
                id = w.Id, name = w.Name, text = w.Text, icon = w.Icon, iconAvailable = waypointIcons.Contains(w.Icon), color = w.Color,
                x = w.X, y = w.Y, z = w.Z, pinned = w.Pinned
            }).ToArray(), true); return true;
        }
        if (method == "GET" && path == "api/v1/hidden-regions")
        {
            Json(context, notebook.Regions.Select(r => new { id = r.Id, name = principal?.IsAdmin == true ? r.Name : "", minX = r.MinX, minZ = r.MinZ, maxX = r.MaxX, maxZ = r.MaxZ, hideInGame = r.HideInGame }).ToArray(), true); return true;
        }
        if (method == "GET" && path == "api/v1/route-shares")
        {
            var shared = notebook.Shared(context.Request.QueryString["id"] ?? "");
            if (shared == null) { NotFound(context); return true; }
            if (!RouteAllowed(principal, shared)) { Error(context, 403, "This route intersects a hidden region"); return true; }
            Json(context, RouteView(shared), true); return true;
        }
        if (principal == null) { Error(context, 401, "Login required"); return true; }
        if (method == "GET" && path == "api/v1/routes")
        {
            Json(context, notebook.ForOwner(principal.PlayerUid).Where(r => RouteAllowed(principal, r)).Select(RouteView).ToArray(), true); return true;
        }
        if (method != "POST" && method != "DELETE") { Error(context, 405, "Method not allowed"); return true; }
        if (context.Request.Headers["X-ServerMap-Request"] != "1") { Error(context, 403, "Missing request header"); return true; }
        if (path == "api/v1/hidden-regions" && !CurrentAdmin(principal)) { Error(context, 403, "Admin login required"); return true; }
        try
        {
            if (method == "DELETE")
            {
                var id = context.Request.QueryString["id"] ?? "";
                var removed = path == "api/v1/routes" ? notebook.Remove(principal.PlayerUid, id) : path == "api/v1/hidden-regions" && notebook.RemoveRegion(id);
                if (!removed) { NotFound(context); return true; }
                if (path == "api/v1/hidden-regions") events.Publish("visibility", new { changed = true });
                Json(context, new { removed = true }, true); return true;
            }
            using var doc = ReadJson(context.Request); var value = doc.RootElement;
            string S(string key, string fallback = "") => value.TryGetProperty(key, out var p) ? p.GetString() ?? fallback : fallback;
            if (path == "api/v1/routes")
            {
                double[][] points;
                var shareId = S("shareId");
                var name = S("name", "Route"); var color = S("color", "#ffd000");
                if (!string.IsNullOrEmpty(shareId))
                {
                    var shared = notebook.Shared(shareId) ?? throw new KeyNotFoundException();
                    if (!RouteAllowed(principal, shared)) throw new UnauthorizedAccessException();
                    points = shared.Points; name = shared.Name; color = shared.Color;
                }
                else points = value.GetProperty("points").Deserialize<double[][]>()!;
                MapNotebookStore.ValidatePoints(points);
                if (!principal.IsAdmin && !MapVisibility.RouteVisible(notebook.Regions, points)) throw new UnauthorizedAccessException();
                Json(context, RouteView(notebook.Save(principal.PlayerUid, string.IsNullOrEmpty(shareId) ? S("id") : null, name, color, points)), true);
            }
            else if (path == "api/v1/route-shares") Json(context, new { id = notebook.ShareRoute(principal.PlayerUid, S("id")) }, true);
            else
            {
                var region = notebook.SaveRegion(S("id"), S("name"), value.GetProperty("minX").GetDouble(), value.GetProperty("minZ").GetDouble(), value.GetProperty("maxX").GetDouble(), value.GetProperty("maxZ").GetDouble(), value.TryGetProperty("hideInGame", out var hide) && hide.GetBoolean());
                events.Publish("visibility", new { changed = true });
                Json(context, new { id = region.Id }, true);
            }
        }
        catch (UnauthorizedAccessException) { Error(context, 403, "Operation is not allowed for this player or region"); }
        catch (KeyNotFoundException) { NotFound(context); }
        catch (InvalidOperationException ex) { Error(context, 409, ex.Message); }
        catch (Exception ex) when (ex is ArgumentException or JsonException or FormatException or OverflowException) { Error(context, 400, "Invalid map notebook request"); }
        return true;
    }
    private bool CurrentAdmin(MapAuthStore.Principal principal) => principal.IsAdmin;
}
