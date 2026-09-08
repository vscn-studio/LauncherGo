using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Vintagestory.API.MathTools;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

namespace ServerMap.Web;

public sealed partial class ServerMapWebServer
{
    private readonly SemaphoreSlim waypointRequests = new(16, 16);
    private static GameWaypointSnapshot.Marker SnapshotWaypoint(Waypoint w) => new(
        w.Guid ?? $"{w.OwningPlayerUid}:{w.Position.X:R}:{w.Position.Z:R}:{w.Title}", w.OwningPlayerUid,
        w.Title ?? "Waypoint", w.Text ?? "", w.Icon ?? "circle", ColorUtil.Int2Hex(w.Color), w.Position.X, w.Position.Y, w.Position.Z, w.Pinned);
    private object WaypointView(GameWaypointSnapshot.Marker w) => new
    { id = w.Id, name = w.Name, text = w.Text, icon = w.Icon, iconAvailable = waypointIcons.Contains(w.Icon), color = w.Color, x = w.X, y = w.Y, z = w.Z, pinned = w.Pinned };

    private T OnGameThread<T>(Func<T> action)
    {
        if (stop.IsCancellationRequested || !waypointRequests.Wait(0)) throw new TimeoutException();
        try
        {
            var call = new GameThreadCall<T>(() => { stop.Token.ThrowIfCancellationRequested(); return action(); });
            api.Event.EnqueueMainThreadTask(call.Run, "servermap-waypoint-write");
            try { return call.Task.WaitAsync(TimeSpan.FromSeconds(8), stop.Token).GetAwaiter().GetResult(); }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
            {
                if (call.CancelPending()) throw new TimeoutException("Game thread busy; no change was made");
                // Once started, report the actual result rather than a false timeout
                // that could cause the user to retry an already committed mutation.
                return call.Task.GetAwaiter().GetResult();
            }
        }
        finally { waypointRequests.Release(); }
    }

    private bool WaypointRequest(HttpListenerContext context, string path)
    {
        if (path != "api/v1/waypoint-options" && path != "api/v1/waypoint-shares" && path != "api/v1/my-waypoints") return false;
        var method = context.Request.HttpMethod;
        if (path == "api/v1/my-waypoints" && method == "GET") return false;
        if (method != "GET" && method != "POST" && method != "DELETE" || method == "DELETE" && path != "api/v1/my-waypoints" || path == "api/v1/waypoint-options" && method != "GET")
        { Error(context, 405, "Method not allowed"); return true; }
        var principal = Principal(context.Request);
        if (principal == null && !(path == "api/v1/waypoint-shares" && method == "GET")) { Error(context, 401, "Login required"); return true; }
        if (method != "GET" && context.Request.Headers["X-ServerMap-Request"] != "1") { Error(context, 403, "Missing request header"); return true; }
        try
        {
            using var document = method == "POST" ? ReadJson(context.Request) : null;
            var value = document?.RootElement ?? default;
            string S(string key, string fallback = "") => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var p) ? p.GetString() ?? fallback : fallback;
            var result = OnGameThread<object>(() =>
            {
                var current = Principal(context.Request);
                if (principal != null && current?.PlayerUid != principal.PlayerUid) throw new UnauthorizedAccessException();
                var layer = api.ModLoader.GetModSystem<WorldMapManager>()?.MapLayers.OfType<WaypointMapLayer>().FirstOrDefault() ?? throw new InvalidOperationException("Game waypoints are not ready");
                if (path == "api/v1/waypoint-options") return new
                {
                    icons = waypointIcons.Names.Select(name => new { name, available = true }).Prepend(new { name = "circle", available = waypointIcons.Contains("circle") }).DistinctBy(i => i.name).ToArray(),
                    colors = layer.WaypointColors.Select(ColorUtil.Int2Hex).ToArray(),
                    enabled = api.World.Config.GetBool("allowMap", true)
                };
                if (method != "GET" && !api.World.Config.GetBool("allowMap", true)) throw new InvalidOperationException("Game map is disabled");
                if (path == "api/v1/waypoint-shares" && method == "GET")
                {
                    var shared = waypointShares.Get(context.Request.QueryString["id"] ?? "") ?? throw new KeyNotFoundException();
                    if (!layer.Waypoints.Any(w => w.OwningPlayerUid == shared.OwnerUid && SnapshotWaypoint(w).Id == shared.Id)) throw new KeyNotFoundException();
                    if (!CanView(current, shared.X, shared.Z)) throw new UnauthorizedAccessException();
                    return WaypointView(shared);
                }
                var owner = current!.PlayerUid;
                var id = method == "DELETE" ? context.Request.QueryString["id"] ?? "" : S("id");
                var existing = string.IsNullOrEmpty(id) ? null : layer.Waypoints.FirstOrDefault(w => w.OwningPlayerUid == owner && SnapshotWaypoint(w).Id == id) ?? throw new KeyNotFoundException();
                if (path == "api/v1/waypoint-shares")
                {
                    if (existing == null) throw new KeyNotFoundException();
                    var marker = SnapshotWaypoint(existing);
                    if (!CanView(current, marker.X, marker.Z)) throw new UnauthorizedAccessException();
                    return new { id = waypointShares.Create(owner, marker) };
                }
                var before = layer.Waypoints.ToArray();
                Waypoint? waypoint = null;
                if (method == "DELETE")
                {
                    if (existing == null) throw new KeyNotFoundException();
                }
                else
                {
                    GameWaypointSnapshot.Marker marker;
                    var shareId = S("shareId");
                    if (shareId.Length > 0)
                    {
                        if (id.Length > 0) throw new ArgumentException("Cannot overwrite using a share");
                        marker = waypointShares.Get(shareId) ?? throw new KeyNotFoundException();
                        if (!layer.Waypoints.Any(w => w.OwningPlayerUid == marker.OwnerUid && SnapshotWaypoint(w).Id == marker.Id)) throw new KeyNotFoundException();
                    }
                    else marker = new("", owner, S("name").Trim(), S("text"), S("icon", "circle"), S("color", "#ffffff"),
                        value.GetProperty("x").GetDouble(), value.TryGetProperty("y", out var y) ? y.GetDouble() : api.World.DefaultSpawnPosition?.Y ?? 0,
                        value.GetProperty("z").GetDouble(), value.TryGetProperty("pinned", out var pinned) && pinned.GetBoolean());
                    if (marker.Name.Length is < 1 or > 80 || marker.Text.Length > 1024 || !Regex.IsMatch(marker.Color,"^#[0-9a-fA-F]{6}$") || !Regex.IsMatch(marker.Icon,"^[a-zA-Z0-9_-]{1,80}$")
                        || !double.IsFinite(marker.X) || !double.IsFinite(marker.Y) || !double.IsFinite(marker.Z) || Math.Abs(marker.X)>32000000 || Math.Abs(marker.Z)>32000000 || marker.Y<0 || marker.Y>api.World.BlockAccessor.MapSizeY) throw new ArgumentException("Invalid waypoint");
                    if (shareId.Length == 0 && marker.Icon != "circle" && marker.Icon != existing?.Icon && !waypointIcons.Contains(marker.Icon) && !layer.WaypointIcons.ContainsKey(marker.Icon)) throw new ArgumentException("Unknown waypoint icon");
                    if (!CanView(current, marker.X, marker.Z) || existing != null && !CanView(current, existing.Position.X, existing.Position.Z)) throw new UnauthorizedAccessException();
                    if (existing == null && layer.Waypoints.Count(w => w.OwningPlayerUid == owner) >= 2000) throw new InvalidOperationException("Waypoint limit reached (2000)");
                    waypoint = new Waypoint { Guid = existing?.Guid ?? Guid.NewGuid().ToString("N"), OwningPlayerUid = owner, OwningPlayerGroupId = existing?.OwningPlayerGroupId ?? -1,
                        Position = new Vec3d(marker.X, marker.Y, marker.Z), Title = marker.Name, Text = marker.Text, Icon = marker.Icon,
                        Color = unchecked((int)0xff000000) | Convert.ToInt32(marker.Color[1..],16), Pinned = marker.Pinned, ShowInWorld = existing?.ShowInWorld ?? false };
                }
                if (existing != null && waypoint != null) layer.Waypoints[layer.Waypoints.IndexOf(existing)] = waypoint;
                else if (existing != null) layer.Waypoints.Remove(existing);
                else if (waypoint != null) layer.Waypoints.Add(waypoint);
                try { api.WorldManager.SaveGame.StoreData("playerMapMarkers_v2", SerializerUtil.Serialize(layer.Waypoints)); }
                catch { layer.Waypoints.Clear(); layer.Waypoints.AddRange(before); throw; }
                CaptureWaypoints();
                foreach (var player in api.World.AllOnlinePlayers.OfType<Vintagestory.API.Server.IServerPlayer>())
                {
                    if (player.PlayerUID != owner && !(existing?.OwningPlayerGroupId >= 0 && player.ServerData.PlayerGroupMemberships.ContainsKey(existing.OwningPlayerGroupId))) continue;
                    try {
#pragma warning disable CS0618 // This public compatibility entry point performs the game's own owner/group filtering and resend.
                        layer.OnViewChangedServer(player, new List<FastVec2i>(), new List<FastVec2i>());
#pragma warning restore CS0618
                    } catch (Exception ex) { api.Logger.Warning("ServerMap waypoint client refresh failed: {0}", ex.Message); }
                }
                return waypoint == null ? new { removed = true } : WaypointView(SnapshotWaypoint(waypoint));
            });
            Json(context, result, true);
        }
        catch (TimeoutException) { Error(context, 503, "Game thread busy; retry later"); }
        catch (UnauthorizedAccessException) { Error(context, 403, "Operation is not allowed for this player or region"); }
        catch (KeyNotFoundException) { NotFound(context); }
        catch (InvalidOperationException ex) { Error(context, 409, ex.Message); }
        catch (Exception ex) when (ex is ArgumentException or JsonException or FormatException or OverflowException) { Error(context, 400, "Invalid waypoint request"); }
        return true;
    }
}
