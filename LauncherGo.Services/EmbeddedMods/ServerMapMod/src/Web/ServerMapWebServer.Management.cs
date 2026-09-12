using System.Net;
using System.Text.Json;

namespace ServerMap.Web;

public sealed partial class ServerMapWebServer
{
    private readonly PlayerTrackStore trackStore;
    private readonly DailyTeleportQuota teleportQuota;
    private long trackingListener;
    private MapManagementSettings Management => announcements.Current.Management ?? new() { PoiQuota = config.MaxPoisPerPlayer };
    private bool ManagementRequest(HttpListenerContext context, string path)
    {
        if (path is not ("api/v1/moments" or "api/v1/moments/like" or "api/v1/admin/images" or "api/v1/admin/tracks" or "api/v1/admin/online-players" or "api/v1/admin/track-mount-image")) return false;
        var principal = Principal(context.Request);
        var admin = path.StartsWith("api/v1/admin/", StringComparison.Ordinal);
        if (admin && principal?.IsAdmin != true) { Error(context, 403, "Admin login required"); return true; }
        var method = context.Request.HttpMethod;
        if ((path is "api/v1/moments" or "api/v1/moments/like") && method != "GET" && principal == null) { Error(context, 401, "Login required"); return true; }
        if (method != "GET" && context.Request.Headers["X-ServerMap-Request"] != "1") { Error(context, 403, "Missing request header"); return true; }
        try
        {
            if (path == "api/v1/admin/track-mount-image" && method == "GET")
            {
                var image = trackStore.MountImage(context.Request.QueryString["id"] ?? "", context.Request.QueryString["key"]);
                if (image == null) NotFound(context);
                else { context.Response.Headers["X-Content-Type-Options"] = "nosniff"; context.Response.Headers["Vary"] = "Cookie"; ServeFile(context, image, "image/png", true); }
                return true;
            }
            if (path is "api/v1/moments" or "api/v1/moments/like" or "api/v1/admin/images")
            {
                if (method == "GET" && (path == "api/v1/moments" || path == "api/v1/admin/images"))
                {
                    var query = context.Request.QueryString["q"] ?? "";
                    var points = pois.All.Where(p => p.ImageKey != null && (admin || announcements.Current.PoiImagesEnabled && !Management.Layer("pois").Forbidden && PoiVisible(principal, p)))
                        .Where(p => string.IsNullOrEmpty(query) || p.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || p.Text.Contains(query, StringComparison.OrdinalIgnoreCase) || ImageAuthor(p).Contains(query, StringComparison.OrdinalIgnoreCase));
                    var skip = int.TryParse(context.Request.QueryString["skip"], out var offset) ? Math.Max(0, offset) : 0;
                    var sort = context.Request.QueryString["sort"];
                    var all = (sort?.Equals("likes", StringComparison.OrdinalIgnoreCase) == true
                        ? points.OrderByDescending(p => momentLikes.Count(p.Id)).ThenByDescending(p => p.UpdatedAt)
                        : points.OrderByDescending(p => p.UpdatedAt)).ToArray();
                    Json(context, new { total = all.Length, items = all.Skip(skip).Take(100).Select(p => MomentView(p, principal)) }, true);
                }
                else if (method == "POST" && (path == "api/v1/moments/like" || path == "api/v1/moments"))
                {
                    using var document = ReadJson(context.Request);
                    var id = document.RootElement.TryGetProperty("id", out var idValue) ? idValue.GetString() : context.Request.QueryString["id"];
                    var point = pois.All.FirstOrDefault(p => p.Id == id && p.ImageKey != null && announcements.Current.PoiImagesEnabled && !Management.Layer("pois").Forbidden && PoiVisible(principal, p));
                    if (point == null) { NotFound(context); return true; }
                    var desired = document.RootElement.TryGetProperty("liked", out var likedValue) && likedValue.ValueKind is JsonValueKind.True or JsonValueKind.False ? likedValue.GetBoolean() : (bool?)null;
                    var result = desired.HasValue ? momentLikes.Set(point.Id, principal!.PlayerUid, desired.Value) : momentLikes.Toggle(point.Id, principal!.PlayerUid);
                    Json(context, new { liked = result.Liked, isLiked = result.Liked, likes = result.Count, likeCount = result.Count, id = point.Id }, true);
                }
                else if (admin && method == "DELETE")
                {
                    if (!poiWrites.Wait(0)) { Error(context, 429, "POI save busy; please retry"); return true; }
                    try
                    {
                        if (Principal(context.Request)?.IsAdmin != true) { Error(context, 403, "Admin login required"); return true; }
                        var point = pois.All.FirstOrDefault(p => p.Id == context.Request.QueryString["id"]);
                        if (point == null) { NotFound(context); return true; }
                        // Require the displayed key, so a stale manager cannot delete a replacement.
                        if (point.ImageKey != context.Request.QueryString["key"]) { Error(context, 409, "Image changed; refresh"); return true; }
                        pois.TrySave(point with { ImageKey = null, ImageAddedBy = null, ImageAddedByUid = null }, principal!.PlayerUid, Management.PoiQuota, true, out _, true);
                        momentLikes.Clear(point.Id);
                        // Keep sanitized files for recovery; the detached key is no longer servable.
                        events.Publish("layer", new { layer = "pois", version = layerVersions.AddOrUpdate("pois", 2, (_, n) => n + 1) });
                        Json(context, new { removed = true }, true);
                    }
                    finally { poiWrites.Release(); }
                }
                else Error(context, 405, "Method not allowed");
                return true;
            }
            if (path == "api/v1/admin/online-players" && method == "GET")
            {
                Json(context, OnGameThread(() => api.World.AllOnlinePlayers.Select(p => new { uid = p.PlayerUID, name = p.PlayerName }).ToArray()), true); return true;
            }
            if (path == "api/v1/admin/tracks")
            {
                if (method == "GET")
                {
                    var id = context.Request.QueryString["id"];
                    DateTimeOffset? after = DateTimeOffset.TryParse(context.Request.QueryString["after"], out var since) ? since : null;
                    if (id == null) Json(context, trackStore.List(context.Request.QueryString["q"]), true);
                    else if (trackStore.Get(id, after) is { } track) Json(context, track, true);
                    else NotFound(context);
                }
                else if (method == "DELETE")
                {
                    var removed = OnGameThread(() =>
                    {
                        if (Principal(context.Request)?.IsAdmin != true) throw new UnauthorizedAccessException();
                        return trackStore.Remove(context.Request.QueryString["id"] ?? "");
                    });
                    if (removed) Json(context, new { removed = true }, true);
                    else NotFound(context);
                }
                else if (method == "POST")
                {
                    using var document = ReadJson(context.Request); var input = document.RootElement;
                    var result = OnGameThread(() =>
                    {
                        var current = Principal(context.Request);
                        if (current?.IsAdmin != true) throw new UnauthorizedAccessException();
                        if (input.GetProperty("action").GetString() == "stop")
                        {
                            var id = input.GetProperty("id").GetString() ?? "";
                            trackStore.Stop(id, "manual"); return trackStore.Get(id);
                        }
                        if (input.GetProperty("action").GetString() != "start") throw new ArgumentException("Invalid tracking action");
                        var uid = input.GetProperty("uid").GetString();
                        var player = api.World.AllOnlinePlayers.FirstOrDefault(p => p.PlayerUID == uid) ?? throw new ArgumentException("Player is offline");
                        var track = trackStore.Start(player.PlayerUID, player.PlayerName, current.PlayerUid, input.GetProperty("seconds").GetInt32());
                        CaptureTracks();
                        return trackStore.Get(track.Id);
                    });
                    Json(context, result!, true);
                }
                else Error(context, 405, "Method not allowed");
            }
            else Error(context, 405, "Method not allowed");
        }
        catch (UnauthorizedAccessException) { Error(context, 403, "Admin login required"); }
        catch (TimeoutException) { Error(context, 503, "Game thread busy"); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or JsonException or KeyNotFoundException)
        { Error(context, 400, ex.Message); }
        return true;
    }
    private string ImageAuthor(PoiStore.Poi point) => point.ImageAddedBy ?? auth.PlayerName(point.OwnerUid) ?? point.OwnerUid;
    private string? MomentAvatar(PoiStore.Poi point)
    {
        var uid = point.ImageAddedByUid ?? point.OwnerUid;
        var player = api.World.AllOnlinePlayers.FirstOrDefault(p => p.PlayerUID == uid);
        if (player != null) return PlayerAvatar(player);
        var key = ClientAvatars?.GetLastKey(uid) ?? alliances.Avatar(uid);
        return key == null ? null : "api/v1/avatars/" + key + ".png";
    }
    private object MomentView(PoiStore.Poi point, MapAuthStore.Principal? principal)
    {
        var likes = momentLikes.Count(point.Id);
        var liked = momentLikes.HasLiked(point.Id, principal?.PlayerUid);
        var author = ImageAuthor(point);
        var avatar = MomentAvatar(point);
        return new
        {
            id = point.Id,
            title = point.Name,
            description = point.Text,
            name = point.Name,
            text = point.Text,
            author,
            playerName = author,
            avatar,
            playerAvatar = avatar,
            imageKey = point.ImageKey,
            updatedAt = point.UpdatedAt,
            location = point.Name,
            likes,
            likeCount = likes,
            liked,
            isLiked = liked
        };
    }
    private void CaptureTracks()
    {
        RefreshExplorationGroups();
        foreach (var track in trackStore.Active)
        {
            try
            {
                if (DateTimeOffset.UtcNow >= track.Deadline) { trackStore.Stop(track.Id, "duration"); continue; }
                if (!CurrentPrincipal(new(track.AdminUid, "", false)).IsAdmin) { trackStore.Stop(track.Id, "admin-revoked"); continue; }
                var player = api.World.AllOnlinePlayers.FirstOrDefault(p => p.PlayerUID == track.PlayerUid);
                if (player?.Entity?.Pos is not { } pos || pos.Dimension != 0) { trackStore.Stop(track.Id, "offline-or-dimension"); continue; }
                var mount = Mounts.Active.FirstOrDefault(m => m.Riders.Any(r => r.Uid == track.PlayerUid));
                PlayerTrackStore.MountPoint? mountPoint = mount == null ? null : new(mount.Name, mount.X, mount.Y, mount.Z, mount.Yaw);
                if (mount != null && Mounts.Get(mount.Id) is { } image)
                {
                    trackStore.SaveMountImage(image.Key, image.Png);
                    mountPoint = mountPoint! with { ImageKey = image.Key, WorldSize = image.WorldSize, CenterX = image.CenterX, CenterZ = image.CenterZ, DisplayScale = MountSnapshotStore.DisplayScale(image.Footprint) };
                }
                trackStore.Append(track.Id, new(DateTimeOffset.UtcNow, pos.X, pos.Y, pos.Z, pos.Yaw,
                    mountPoint));
            }
            catch (Exception ex) { api.Logger.Warning("ServerMap tracking failed: {0}", ex.Message); }
        }
    }
    private void CheckTeleportQuota(string uid, bool admin)
    {
        if (!admin && !teleportQuota.Available(uid, Management.DailyTeleports)) throw new TeleportError(409, "teleport_daily_quota");
    }
    private IDisposable ReserveTeleportQuota(IEnumerable<string> uids, out Action commit)
    {
        try { return teleportQuota.Reserve(uids, Management.DailyTeleports, out commit); }
        catch (InvalidOperationException) { throw new TeleportError(409, "teleport_daily_quota"); }
    }
}
