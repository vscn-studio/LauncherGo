using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ServerMap.Configuration;
using ServerMap.Render;
using ServerMap.World;
using ServerMap.Util;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace ServerMap.Web;

public sealed partial class ServerMapWebServer : IDisposable
{
    private static readonly byte[] TransparentTile = PngEncoder.Encode(TilePyramidBuilder.TileSize, TilePyramidBuilder.TileSize, new byte[TilePyramidBuilder.TileSize * TilePyramidBuilder.TileSize * 4]);
    private static readonly string[] Renderers = ["basic", "sepia"];
    private static readonly string[] Layers = ["players", "spawn", "claims", "claim-areas", "chunks", "translocators", "pois"];
    private readonly ICoreServerAPI api; private readonly ServerMapConfig config; private readonly string root; private readonly string webRoot;
    private readonly WorldDatabaseReader reader; private readonly MapPalette materials; private readonly MapRenderer renderer; private readonly TilePyramidBuilder pyramid;
    private readonly MapAuthStore auth; private readonly PoiStore pois; private readonly AnnouncementStore announcements;
    private readonly CancellationTokenSource stop = new(); private readonly LiveEventHub events = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<(int X, int Z), byte>> baseTiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<(int X, int Z), byte> knownRegions = new();
    private readonly ConcurrentDictionary<string, long> layerVersions = new(StringComparer.OrdinalIgnoreCase);
    private readonly TranslocatorIndex translocators;
    private readonly object maintenanceGate = new();
    private Task maintenance = Task.CompletedTask;
    private bool maintenanceStarted;
    private readonly DateTimeOffset startedAt = DateTimeOffset.UtcNow;
    private HttpListener? listener;

    public ServerMapWebServer(ICoreServerAPI api, ServerMapConfig config, string root, WorldDatabaseReader reader, MapPalette materials, MapAuthStore auth, PoiStore pois, AnnouncementStore announcements)
    {
        this.api = api; this.config = config; this.root = root; this.reader = reader; this.materials = materials; this.auth = auth; this.pois = pois; this.announcements = announcements;
        translocators = new TranslocatorIndex(Path.Combine(root, "translocators.json"), message => api.Logger.Warning(message));
        notebook = new MapNotebookStore(Path.Combine(root, "web-notebook.json"));
        InitializeNotebook();
        InitializeAvatars();
        webRoot = ResolveWebRoot(api); renderer = new MapRenderer(reader, root, api.World.BlockAccessor.MapSizeY, materials); pyramid = new TilePyramidBuilder(root);
        foreach (var name in Renderers) { baseTiles[name] = LoadBaseTiles(name); layerVersions[name] = 1; }
        foreach (var region in baseTiles.Values.SelectMany(tiles => tiles.Keys)) knownRegions.TryAdd(region, 0);
        foreach (var name in Layers) layerVersions.TryAdd(name, 1);
        api.Logger.Notification("ServerMap 2D web root: {0}", webRoot);
    }

    public void Start()
    {
        if (!config.Enabled) return;
        if (!IsLoopback(config.BindAddress) && string.IsNullOrWhiteSpace(config.Token)) { api.Logger.Error("ServerMap refuses public binding without a Token."); return; }
        listener = new HttpListener(); listener.Prefixes.Add($"http://{config.BindAddress}:{config.Port}/"); listener.Start(); _ = Loop(); api.Logger.Notification("ServerMap web server listening on {0}:{1}", config.BindAddress, config.Port);
    }

    private async Task Loop()
    {
        while (!stop.IsCancellationRequested && listener != null)
        {
            try { var context = await listener.GetContextAsync(); _ = Task.Run(() => Handle(context), stop.Token); }
            catch { if (!stop.IsCancellationRequested) api.Logger.Warning("ServerMap HTTP listener stopped unexpectedly"); }
        }
    }

    private void Handle(HttpListenerContext context)
    {
        try
        {
            var path = context.Request.Url?.AbsolutePath.Trim('/') ?? "";
            if (path.StartsWith("servermap/", StringComparison.OrdinalIgnoreCase)) path = path[10..];
            if (NotebookRequest(context, path)) return;
            if (path.StartsWith("api/v1/avatars/", StringComparison.Ordinal))
            {
                var key = path[15..];
                if (!System.Text.RegularExpressions.Regex.IsMatch(key, "^[a-f0-9]{64}\\.png$")) { NotFound(context); return; }
                var image = ClientAvatars?.Get(key[..^4]) ?? avatars?.Get(key[..^4]);
                if (image == null) { NotFound(context); return; }
                context.Response.Headers["X-Content-Type-Options"] = "nosniff";
                ServeBytes(context, image, "image/png", "public, max-age=86400, immutable"); return;
            }
            if (path is "" or "servermap" or "index.html") { ServeBytes(context, Encoding.UTF8.GetBytes((announcements.Current.Site ?? new()).ApplyToHtml(File.ReadAllText(Path.Combine(webRoot, "index.html")))), "text/html; charset=utf-8", "no-store"); return; }
            if (path.StartsWith("vendor/", StringComparison.OrdinalIgnoreCase) || path.StartsWith("assets/", StringComparison.OrdinalIgnoreCase) || path is "mobile.css" or "notebook.css" or "notebook.js" or "screenshot.js") { ServeWebAsset(context, path); return; }
            if (path == "api/v1/events") { events.Subscribe(context, stop.Token).GetAwaiter().GetResult(); return; }
            if (path == "api/v1/auth/login" && context.Request.HttpMethod == "POST") { Login(context); return; }
            if (path == "api/v1/auth/logout" && context.Request.HttpMethod == "POST") { auth.Logout(context.Request.Cookies["servermap_auth"]?.Value); context.Response.Headers["Set-Cookie"] = "servermap_auth=; Path=/; Max-Age=0; HttpOnly; SameSite=Strict"; Json(context, new { authenticated = false }, true); return; }
            if (path == "api/v1/auth/me") { var principal = Principal(context.Request); Json(context, principal == null ? new { authenticated = false } : new { authenticated = true, name = principal.PlayerName, admin = principal.IsAdmin }, true); return; }
            if (path == "api/v1/announcement") { HandleAnnouncement(context); return; }
            if (path == "api/v1/status") { Json(context, Status(), true); return; }
            if (path == "api/v1/search") { Json(context, Search(context.Request.QueryString["q"], Principal(context.Request)), true); return; }
            if (path == "api/v1/pois") { HandlePois(context); return; }
            if (path is "api/v1/teleport" or "api/v1/teleport/quote") { HandleTeleport(context, path.EndsWith("/quote", StringComparison.Ordinal)); return; }
            if (path == "api/v1/height") { Height(context); return; }
            if (path == "api/v1/map/metadata") { Json(context, Metadata(), true); return; }
            if (path == "api/v1/settings") { Json(context, Settings(), true); return; }
            if (path == "api/v1/layers/manifest") { Json(context, Manifest()); return; }
            if (path.StartsWith("api/v1/layers/", StringComparison.OrdinalIgnoreCase)) { var name = path[14..]; if (!Layers.Contains(name, StringComparer.OrdinalIgnoreCase)) { NotFound(context); return; } Json(context, Layer(name, context.Request.QueryString["bbox"], Principal(context.Request)), true); return; }
            if (path.StartsWith("api/v1/tiles/", StringComparison.OrdinalIgnoreCase)) { ServePyramidTile(context, path[13..]); return; }
            if (path.StartsWith("api/v1/2d/", StringComparison.OrdinalIgnoreCase)) { ServeLegacyTile(context, path[10..]); return; }
            if (path == "api/v1/players") { Json(context, Players(Principal(context.Request)), true); return; }
            NotFound(context);
        }
        catch (Exception ex) { if (!IsClientDisconnect(ex)) api.Logger.Warning("ServerMap HTTP error: {0}", ex); try { context.Response.StatusCode = 500; context.Response.Close(); } catch { } }
    }

    private void ServePyramidTile(HttpListenerContext context, string route)
    {
        var parts = route.Split('/');
        if (parts.Length != 3 || !Renderers.Contains(parts[0], StringComparer.OrdinalIgnoreCase) || !int.TryParse(parts[1], out var zoom) || !TryTileName(parts[2], out var x, out var z) || zoom is < 0 or > TilePyramidBuilder.MaxZoom) { NotFound(context); return; }
        ServeTile(context, parts[0], zoom, x, z);
    }
    private void ServeLegacyTile(HttpListenerContext context, string route)
    {
        var parts = route.Split('/');
        if (parts.Length != 3 || !Renderers.Contains(parts[0], StringComparer.OrdinalIgnoreCase) || parts[1] != "0" || !TryTileName(parts[2], out var x, out var z)) { NotFound(context); return; }
        ServeTile(context, parts[0], 0, x, z);
    }
    private void ServeTile(HttpListenerContext context, string rendererName, int zoom, int x, int z)
    {
        if (Math.Abs((long)x) > 1_048_576 || Math.Abs((long)z) > 1_048_576) { NotFound(context); return; }
        var path = Path.Combine(root, "2d", rendererName.ToLowerInvariant(), zoom.ToString(CultureInfo.InvariantCulture), $"{x}_{z}.png");
        var exists = File.Exists(path);
        var bytes = exists ? File.ReadAllBytes(path) : TransparentTile;
        if (MapVisibility.ShouldMaskTiles(Principal(context.Request)?.IsAdmin == true, context.Request.QueryString["hideRegions"]))
            bytes = MapVisibility.MaskTile(bytes, zoom, x, z, notebook.Regions);
        context.Response.Headers["Vary"] = "Cookie";
        ServeBytes(context, bytes, "image/png", "no-store");
    }
    private static bool TryTileName(string value, out int x, out int z)
    {
        x = z = 0; if (!value.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) return false;
        var coordinates = value[..^4].Split('_');
        return coordinates.Length == 2 && int.TryParse(coordinates[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out x) && int.TryParse(coordinates[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out z);
    }

    private MapAuthStore.Principal? Principal(HttpListenerRequest request)
    {
        var principal = auth.AuthenticateSession(request.Cookies["servermap_auth"]?.Value);
        return principal == null ? null : CurrentPrincipal(principal);
    }

    private MapAuthStore.Principal CurrentPrincipal(MapAuthStore.Principal principal)
    {
        // Never retain map admin access merely because root was present when
        // the password/session was created. Use the game's current authority.
        var player = api.World.AllOnlinePlayers.FirstOrDefault(p => p.PlayerUID == principal.PlayerUid);
        var admin = player?.HasPrivilege("root") ?? (api.World is Vintagestory.Server.ServerMain server
            && api.PlayerData.GetPlayerDataByUid(principal.PlayerUid) is Vintagestory.Server.ServerPlayerData data
            && data.HasPrivilege("root", server.Config.RolesByCode));
        return principal with { IsAdmin = admin };
    }

    private void Login(HttpListenerContext context)
    {
        try
        {
            using var document = ReadJson(context.Request);
            var playerName = document.RootElement.GetProperty("playerName").GetString() ?? "";
            var password = document.RootElement.GetProperty("password").GetString() ?? "";
            var login = auth.Login(playerName, password);
            if (login == null) { Error(context, 401, "Invalid player name or password"); return; }
            context.Response.Headers["Set-Cookie"] = $"servermap_auth={login.Value.SessionId}; Path=/; Max-Age=2592000; HttpOnly; SameSite=Strict";
            var principal = CurrentPrincipal(login.Value.Principal);
            Json(context, new { authenticated = true, name = principal.PlayerName, admin = principal.IsAdmin }, true);
        }
        catch { Error(context, 400, "Invalid login request"); }
    }

    private void HandleAnnouncement(HttpListenerContext context)
    {
        object Response(AnnouncementStore.Announcement value) => new { html = value.Html, serverWebsite = value.ServerWebsite, site = value.Site ?? new(), playerGearTeleportEnabled = value.PlayerGearTeleportEnabled, playerTeleport = value.PlayerTeleport ?? new(), updatedBy = value.UpdatedBy, updatedAt = value.UpdatedAt };
        if (context.Request.HttpMethod == "GET") { Json(context, Response(announcements.Current), true); return; }
        var principal = Principal(context.Request);
        if (context.Request.HttpMethod != "POST") { Error(context, 405, "Method not allowed"); return; }
        if (principal?.IsAdmin != true) { Error(context, 403, "Admin login required"); return; }
        if (!string.Equals(context.Request.Headers["X-ServerMap-Request"], "1", StringComparison.Ordinal)) { Error(context, 403, "Missing request header"); return; }
        try
        {
            using var document = ReadJson(context.Request);
            var html = document.RootElement.GetProperty("html").GetString() ?? "";
            var website = document.RootElement.TryGetProperty("serverWebsite", out var websiteValue) ? websiteValue.GetString() ?? "" : announcements.Current.ServerWebsite;
            var site = document.RootElement.TryGetProperty("site", out var siteValue) ? siteValue.Deserialize<WebPageMetadata>() : null;
            bool? playerTeleport = document.RootElement.TryGetProperty("playerGearTeleportEnabled", out var teleportValue) ? teleportValue.GetBoolean() : null;
            var teleportSettings = document.RootElement.TryGetProperty("playerTeleport", out var settingsValue)
                ? settingsValue.Deserialize<PlayerTeleportSettings>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true })?.Validate()
                    ?? throw new ArgumentException("Missing teleport settings") : null;
            // Serialize setting changes with teleport commits on the game thread:
            // switching off cannot race the final eligibility check and payment.
            var saved = OnGameThread(() =>
            {
                if (Principal(context.Request)?.IsAdmin != true) throw new UnauthorizedAccessException();
                if (teleportSettings != null && api.World.GetItem(new AssetLocation(teleportSettings.ItemCode)) == null
                    && api.World.GetBlock(new AssetLocation(teleportSettings.ItemCode)) == null)
                    throw new ArgumentException("Unknown teleport item code");
                var value = announcements.Save(html, website, principal.PlayerName, site, playerTeleport, teleportSettings);
                events.Publish("settings", new { playerGearTeleportEnabled = value.PlayerGearTeleportEnabled, playerTeleport = value.PlayerTeleport });
                return value;
            });
            Json(context, Response(saved), true);
        }
        catch (UnauthorizedAccessException) { Error(context, 403, "Admin login required"); }
        catch (TimeoutException) { Error(context, 503, "Game thread busy"); }
        catch { Error(context, 400, "Invalid announcement"); }
    }

    private void HandlePois(HttpListenerContext context)
    {
        var principal = Principal(context.Request);
        if (context.Request.HttpMethod == "GET") { Json(context, pois.All.Where(p => PoiVisible(principal, p)), true); return; }
        if (principal == null) { Error(context, 403, "Login required"); return; }
        if (!string.Equals(context.Request.Headers["X-ServerMap-Request"], "1", StringComparison.Ordinal)) { Error(context, 403, "Missing request header"); return; }
        if (context.Request.HttpMethod == "DELETE")
        {
            var id = context.Request.QueryString["id"];
            if (string.IsNullOrWhiteSpace(id) || !pois.Remove(id, principal.PlayerUid, principal.IsAdmin)) { NotFound(context); return; }
            events.Publish("layer", new { layer = "pois", version = layerVersions.AddOrUpdate("pois", 2, (_, old) => old + 1) }); Json(context, new { removed = true }, true); return;
        }
        if (context.Request.HttpMethod != "POST") { Error(context, 405, "Method not allowed"); return; }
        try
        {
            using var document = ReadJson(context.Request); var rootElement = document.RootElement;
            string S(string name, string fallback = "") => rootElement.TryGetProperty(name, out var value) ? value.GetString() ?? fallback : fallback;
            double D(string name, double fallback = 0) => rootElement.TryGetProperty(name, out var value) && value.TryGetDouble(out var number) && double.IsFinite(number) ? number : fallback;
            double? DN(string name) => rootElement.TryGetProperty(name, out var value) && value.TryGetDouble(out var number) ? number : null;
            var id = S("id"); var x = D("x"); var z = D("z");
            if (!CanView(principal, x, z)) { Error(context, 403, "Hidden region"); return; }
            if (string.IsNullOrWhiteSpace(id) && !CanCreatePoiAt(principal, x, z)) { Error(context, 403, "Cannot create a POI inside another player's claim"); return; }
            var input = new PoiStore.Poi(id, S("type", "text"), S("name", "POI"), S("text"), S("color", "#e66c75"), D("rotation"), x, z, DN("x2"), DN("z2"), principal.PlayerUid, DateTimeOffset.UtcNow);
            if (!MapNotebookStore.ValidCoordinate(x) || !MapNotebookStore.ValidCoordinate(z) || input.X2 is { } x2 && !MapNotebookStore.ValidCoordinate(x2) || input.Z2 is { } z2 && !MapNotebookStore.ValidCoordinate(z2)) { Error(context, 400, "Invalid coordinates"); return; }
            if (!PoiVisible(principal, input)) { Error(context, 403, "Hidden region"); return; }
            var result = pois.TrySave(input, principal.PlayerUid, config.MaxPoisPerPlayer, principal.IsAdmin, out var saved);
            if (result == PoiStore.SaveResult.QuotaExceeded) { Error(context, 409, $"POI limit reached ({Math.Max(0, config.MaxPoisPerPlayer)})"); return; }
            if (result == PoiStore.SaveResult.Forbidden) { Error(context, 403, "Cannot edit another player's POI"); return; }
            events.Publish("layer", new { layer = "pois", version = layerVersions.AddOrUpdate("pois", 2, (_, old) => old + 1) }); Json(context, saved!, true);
        }
        catch { Error(context, 400, "Invalid POI"); }
    }

    private bool CanCreatePoiAt(MapAuthStore.Principal principal, double x, double z)
    {
        if (principal.IsAdmin) return true;
        var blockX = (int)Math.Floor(x); var blockZ = (int)Math.Floor(z); var player = api.World.PlayerByUid(principal.PlayerUid);
        foreach (var claim in api.WorldManager.LandClaims)
        {
            if (!claim.Areas.Any(area => area.Contains(blockX, blockZ))) continue;
            if (string.Equals(claim.OwnedByPlayerUid, principal.PlayerUid, StringComparison.Ordinal)) continue;
            if (player != null && claim.TestPlayerAccess(player, EnumBlockAccessFlags.BuildOrBreak) is EnumPlayerAccessResult.OkGroup or EnumPlayerAccessResult.OkGrantedPlayer or EnumPlayerAccessResult.OkGrantedGroup) continue;
            return false;
        }
        return true;
    }

    private object Status()
    {
        var calendar = api.World.Calendar; var spawn = api.World.DefaultSpawnPosition;
        ClimateCondition? climate = null;
        try { if (spawn != null) climate = api.World.BlockAccessor.GetClimateAt(new BlockPos((int)spawn.X, (int)spawn.Y, (int)spawn.Z), EnumGetClimateMode.NowValues); } catch { }
        return new { serverName = api.Server.Config.ServerName, online = api.World.AllOnlinePlayers.Length, maximum = api.Server.Config.MaxClients, uptimeSeconds = api.Server.ServerUptimeSeconds, worldTime = calendar == null ? null : new { date = calendar.PrettyDate(), year = calendar.Year, month = calendar.Month, hour = calendar.HourOfDay, totalDays = calendar.TotalDays, moon = calendar.MoonPhase.ToString() }, environment = climate == null ? null : new { temperature = climate.Temperature, rainfall = climate.Rainfall, cloud = climate.RainCloudOverlay, fertility = climate.Fertility, forest = climate.ForestDensity } };
    }

    private void Height(HttpListenerContext context)
    {
        if (!double.TryParse(context.Request.QueryString["x"], NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ||
            !double.TryParse(context.Request.QueryString["z"], NumberStyles.Float, CultureInfo.InvariantCulture, out var z) ||
            !double.IsFinite(x) || !double.IsFinite(z)) { Error(context, 400, "Invalid coordinates"); return; }
        if (!CanView(Principal(context.Request), x, z)) { NotFound(context); return; }
        var y = reader.SurfaceHeightAt(x, z);
        if (y == null) { NotFound(context); return; }
        Json(context, new { x, y, z }, true);
    }

    private object Search(string? query, MapAuthStore.Principal? principal)
    {
        query = query?.Trim(); if (string.IsNullOrWhiteSpace(query)) return Array.Empty<object>();
        var results = new List<object>(); bool Match(string? value) => value?.Contains(query, StringComparison.OrdinalIgnoreCase) == true;
        results.AddRange(NotebookSearch.Find(query, principal?.PlayerUid, principal?.IsAdmin == true, waypoints, notebook));
        var spawn = api.World.DefaultSpawnPosition;
        if (spawn != null && (Match("出生点") || Match("Spawn")) && CanView(principal, spawn.X, spawn.Z))
            results.Add(new { kind = "spawn", name = "Spawn", hasLocation = true, x = spawn.X, z = spawn.Z });
        foreach (var player in api.World.AllOnlinePlayers) if (Match(player.PlayerName))
        {
            var pos = player.Entity?.Pos; var visible = principal != null && (principal.IsAdmin || principal.PlayerUid == player.PlayerUID) && pos != null && CanView(principal, pos.X, pos.Z);
            results.Add(new { kind = "player", name = player.PlayerName, hasLocation = visible && pos != null, x = visible ? pos?.X : null, z = visible ? pos?.Z : null });
        }
        foreach (var claim in api.WorldManager.LandClaims) if ((Match(claim.Description) || Match(claim.LastKnownOwnerName)) && CanView(principal, claim.Center.X, claim.Center.Z) && (principal?.IsAdmin == true || !claim.Areas.Any(a => notebook.Regions.Any(r => MapVisibility.Intersects(r, a.MinX, a.MinZ, a.MaxX + 1, a.MaxZ + 1))))) { var center = claim.Center; results.Add(new { kind = "claim", name = string.IsNullOrWhiteSpace(claim.Description) ? claim.LastKnownOwnerName : claim.Description, hasLocation = true, x = (double?)center.X, z = (double?)center.Z }); }
        foreach (var point in translocators.Values) if (Match(point.Name) && CanView(principal, point.X, point.Z)) results.Add(new { kind = point.Kind, name = point.Name, hasLocation = true, x = (double?)point.X, z = (double?)point.Z });
        foreach (var poi in pois.All) if ((Match(poi.Name) || Match(poi.Text)) && PoiVisible(principal, poi)) results.Add(new { kind = "poi", name = poi.Name, hasLocation = true, x = (double?)poi.X, z = (double?)poi.Z });
        return results.Take(50).ToArray();
    }

    private static JsonDocument ReadJson(HttpListenerRequest request)
    {
        const int limit = 1024 * 1024;
        if (request.ContentLength64 > limit) throw new InvalidDataException();
        using var buffer = new MemoryStream(); var chunk = new byte[8192]; int count;
        while ((count = request.InputStream.Read(chunk)) > 0) { if (buffer.Length + count > limit) throw new InvalidDataException(); buffer.Write(chunk, 0, count); }
        return JsonDocument.Parse(buffer.ToArray(), new JsonDocumentOptions { MaxDepth = 16 });
    }
    private static void Error(HttpListenerContext context, int status, string message) { context.Response.StatusCode = status; Json(context, new { error = message }, true); }

    private bool Authorized(HttpListenerRequest request) => string.IsNullOrEmpty(config.Token) || string.Equals(request.Headers["Authorization"], "Bearer " + config.Token, StringComparison.Ordinal) || string.Equals(request.QueryString["token"], config.Token, StringComparison.Ordinal);
    private void ServeWebAsset(HttpListenerContext context, string relativePath)
    {
        var parts = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts.Any(part => !Safe(part))) { NotFound(context); return; }
        var type = Path.GetExtension(parts[^1]).ToLowerInvariant() switch { ".css" => "text/css; charset=utf-8", ".mjs" or ".js" => "text/javascript; charset=utf-8", ".png" => "image/png", ".svg" => "image/svg+xml", ".woff" => "font/woff", ".woff2" => "font/woff2", _ => null };
        if (type == null) { NotFound(context); return; }
        var file = parts.Aggregate(webRoot, Path.Combine); ServeFile(context, file, type);
    }
    private static void ServeFile(HttpListenerContext context, string path, string type, bool noStore = false, string? cacheControl = null)
    {
        if (!File.Exists(path)) { NotFound(context); return; }
        byte[] bytes; using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) { bytes = new byte[checked((int)stream.Length)]; stream.ReadExactly(bytes); }
        ServeBytes(context, bytes, type, noStore ? "no-store" : cacheControl ?? "public, max-age=31536000, immutable");
    }
    private static void ServeBytes(HttpListenerContext context, byte[] bytes, string type, string cacheControl)
    {
        var etag = "\"" + Convert.ToHexString(SHA256.HashData(bytes)) + "\""; context.Response.Headers["ETag"] = etag; context.Response.Headers["Cache-Control"] = cacheControl;
        if (cacheControl != "no-store" && string.Equals(context.Request.Headers["If-None-Match"], etag, StringComparison.Ordinal)) { context.Response.StatusCode = 304; context.Response.Close(); return; }
        context.Response.ContentType = type; context.Response.KeepAlive = false; context.Response.SendChunked = true; context.Response.OutputStream.Write(bytes); context.Response.Close();
    }
    private static void Json(HttpListenerContext context, object value, bool noStore = false) => ServeBytes(context, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value)), "application/json; charset=utf-8", noStore ? "no-store" : "public, max-age=15");
    private static void NotFound(HttpListenerContext context) { context.Response.StatusCode = 404; context.Response.Close(); }
    private static bool Safe(string part) => part.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && !part.Contains("..", StringComparison.Ordinal) && !part.Contains('/') && !part.Contains('\\');
    private static bool IsLoopback(string address) => address.Equals("localhost", StringComparison.OrdinalIgnoreCase) || IPAddress.TryParse(address, out var ip) && IPAddress.IsLoopback(ip);
    // 1229 (ERROR_INVALID_CONNECTION) is reported by HttpListener when the
    // browser/proxy closes a keep-alive connection while a response is being
    // written. Treat it like the other normal client disconnect codes.
    private static bool IsClientDisconnect(Exception exception) => exception is OperationCanceledException || exception is HttpListenerException { ErrorCode: 64 or 995 or 1229 } || exception.InnerException != null && IsClientDisconnect(exception.InnerException);

    private object Metadata()
    {
        var all = baseTiles.Values.SelectMany(set => set.Keys).ToArray(); var position = api.World.DefaultSpawnPosition;
        var defaultX = position?.X ?? (all.Length == 0 ? 0 : all.Average(tile => tile.X * 512 + 256d)); var defaultZ = position?.Z ?? (all.Length == 0 ? 0 : all.Average(tile => tile.Z * 512 + 256d));
        var minX = all.Length == 0 ? 0 : all.Min(tile => tile.X) * 512; var minZ = all.Length == 0 ? 0 : all.Min(tile => tile.Z) * 512;
        var maxX = all.Length == 0 ? 512 : (all.Max(tile => tile.X) + 1) * 512; var maxZ = all.Length == 0 ? 512 : (all.Max(tile => tile.Z) + 1) * 512;
        // The full pyramid is always present (or backfilled on startup).
        // Exposing only the current explored span here made a one-region map
        // report zero and prevented every zoom-out level in the browser.
        var maxZoom = TilePyramidBuilder.MaxZoom;
        var span = Math.Max(maxX - minX, maxZ - minZ);
        var maxZoomOut = Math.Clamp((int)Math.Ceiling(Math.Log2(Math.Max(1d, span / 512d))) + 1, 2, 8);
        return new { version = 11, serverName = api.Server.Config.ServerName, updatedAt = startedAt, serverMapVersion = "0.3.1", tileVersion = typeof(ServerMapWebServer).Assembly.ManifestModule.ModuleVersionId.ToString("N"), colorVersion = materials.ClientColormapVersion, tileSize = 512, minZoom = 0, maxZoom, maxZoomOut, origin = new[] { 0, 0 }, scale = 1, zAxis = "positive-down", bounds = new[] { minX, minZ, maxX, maxZ }, spawn = new { x = api.World.DefaultSpawnPosition?.X ?? 0, z = api.World.DefaultSpawnPosition?.Z ?? 0 }, center = new { x = defaultX, z = defaultZ }, renderers = new { basic = baseTiles["basic"].Count, sepia = baseTiles["sepia"].Count }, colormapReady = materials.HasClientColormap };
    }
    private object Settings() => new { version = 7, enable2d = config.Enable2D, colormapReady = materials.HasClientColormap, colormapMonth = materials.ClientColormapMonth, ranges = Metadata() };
    private object Manifest() => new { version = layerVersions.Values.DefaultIfEmpty().Max(), layers = Layers.Select(name => new { id = name, version = layerVersions[name], visible = name is "players" or "spawn" or "claims" or "translocators" or "pois" }).ToArray() };
    private object Players(MapAuthStore.Principal? principal)
    {
        if (!config.PublicPlayers) return Array.Empty<object>();
        return api.World.AllOnlinePlayers.Select(player => principal == null
            ? (object)new { id = player.PlayerUID, name = player.PlayerName, online = true }
            : (principal.IsAdmin || principal.PlayerUid == player.PlayerUID) && player.Entity?.Pos is { } pos && CanView(principal, pos.X, pos.Z)
                ? new { id = player.PlayerUID, name = player.PlayerName, online = true, x = player.Entity?.Pos.X, y = player.Entity?.Pos.Y, z = player.Entity?.Pos.Z }
                : new { id = player.PlayerUID, name = player.PlayerName, online = true, x = (double?)null, y = (double?)null, z = (double?)null }).ToArray();
    }
    private object Layer(string name, string? bbox, MapAuthStore.Principal? principal)
    {
        var bounds = ParseBounds(bbox); var features = new List<object>();
        if (name.Equals("players", StringComparison.OrdinalIgnoreCase) && config.PublicPlayers && principal != null)
        {
            foreach (var player in api.World.AllOnlinePlayers) if ((principal.IsAdmin || principal.PlayerUid == player.PlayerUID) && player.Entity?.Pos is { } pos && InBounds(pos.X, pos.Z, bounds))
            {
                var health = player.Entity.GetBehavior<EntityBehaviorHealth>();
                var hunger = player.Entity.GetBehavior<EntityBehaviorHunger>();
                features.Add(PointFeature("player-" + player.PlayerUID, pos.X, pos.Z, new { name = player.PlayerName, avatar = PlayerAvatar(player), avatarState = ClientAvatars?.GetStatus(player.PlayerUID, PlayerMapSyncSystem.Appearance(player)), y = pos.Y, yaw = pos.Yaw, mode = player.WorldData.CurrentGameMode.ToString(), health = new { current = health?.Health ?? 15, maximum = health?.MaxHealth ?? 15 }, satiety = new { current = hunger?.Saturation ?? 1500, maximum = hunger?.MaxSaturation ?? 1500 }, kind = "player" }));
            }
        }
        else if (name.Equals("spawn", StringComparison.OrdinalIgnoreCase) && api.World.DefaultSpawnPosition is { } spawn && InBounds(spawn.X, spawn.Z, bounds)) features.Add(PointFeature("spawn", spawn.X, spawn.Z, new { name = "出生点", kind = "spawn" }));
        else if (name.Equals("claims", StringComparison.OrdinalIgnoreCase) || name.Equals("claim-areas", StringComparison.OrdinalIgnoreCase))
        {
            var claimIndex = 0;
            foreach (var claim in api.WorldManager.LandClaims)
            {
                if (IsMerchantClaim(claim)) { claimIndex++; continue; }
                var areaIndex = 0;
                foreach (var area in claim.Areas)
                {
                    var minX = area.MinX; var minZ = area.MinZ; var maxX = area.MaxX + 1; var maxZ = area.MaxZ + 1;
                    if (Intersects(minX, minZ, maxX, maxZ, bounds))
                    {
                        var nameValue = string.IsNullOrWhiteSpace(claim.Description) ? claim.LastKnownOwnerName ?? "Land claim" : claim.Description;
                        var color = ClaimColor(claim.ProtectionLevel, claimIndex);
                        if (name.Equals("claim-areas", StringComparison.OrdinalIgnoreCase))
                            features.Add(new { type = "Feature", id = $"claim-area-{claimIndex}-{areaIndex}", geometry = new { type = "Polygon", coordinates = new[] { new[] { new[] { (double)minX, minZ }, new[] { (double)maxX, minZ }, new[] { (double)maxX, maxZ }, new[] { (double)minX, maxZ }, new[] { (double)minX, minZ } } } }, properties = new { name = nameValue, owner = claim.LastKnownOwnerName, protectionLevel = claim.ProtectionLevel, color, kind = "claim-area" } });
                        else
                            features.Add(PointFeature($"claim-label-{claimIndex}-{areaIndex}", (minX + maxX) / 2d, (minZ + maxZ) / 2d, new { name = nameValue, owner = claim.LastKnownOwnerName, protectionLevel = claim.ProtectionLevel, color, kind = "claim" }));
                    }
                    areaIndex++;
                }
                claimIndex++;
            }
        }
        else if (name.Equals("chunks", StringComparison.OrdinalIgnoreCase)) foreach (var region in baseTiles["sepia"].Keys) { var minX = region.X * 512d; var minZ = region.Z * 512d; var maxX = minX + 512; var maxZ = minZ + 512; if (Intersects(minX, minZ, maxX, maxZ, bounds)) features.Add(new { type = "Feature", id = $"region-{region.X}-{region.Z}", geometry = new { type = "Polygon", coordinates = new[] { new[] { new[] { minX, minZ }, new[] { maxX, minZ }, new[] { maxX, maxZ }, new[] { minX, maxZ }, new[] { minX, minZ } } } }, properties = new { state = "generated", kind = "chunk" } }); }
        else if (name.Equals("translocators", StringComparison.OrdinalIgnoreCase)) AddTranslocators(features, bounds, principal);
        else if (name.Equals("pois", StringComparison.OrdinalIgnoreCase))
            foreach (var poi in pois.All)
                if (InBounds(poi.X, poi.Z, bounds) && PoiVisible(principal, poi)) features.Add(PointFeature(poi.Id, poi.X, poi.Z, new { name = poi.Name, text = poi.Text, color = poi.Color, rotation = poi.Rotation, poiType = poi.Type, kind = "poi", editable = principal != null && (principal.IsAdmin || principal.PlayerUid == poi.OwnerUid) }));
        return new { type = "FeatureCollection", version = layerVersions[name], features = VisibleFeatures(features, principal) };
    }
    private static void AddIndexed(List<object> features, IEnumerable<IndexedPoint> points, (double MinX, double MinZ, double MaxX, double MaxZ)? bounds)
    {
        foreach (var point in points) if (InBounds(point.X, point.Z, bounds)) features.Add(PointFeature(point.Id, point.X, point.Z, new { name = point.Name, y = point.Y, kind = point.Kind, targetX = point.TargetX, targetY = point.TargetY, targetZ = point.TargetZ }));
    }

    private void AddTranslocators(List<object> features, (double MinX, double MinZ, double MaxX, double MaxZ)? bounds, MapAuthStore.Principal? principal)
    {
        var emitted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var point in translocators.Values)
        {
            var targetX = point.TargetX; var targetZ = point.TargetZ;
            var sourceVisible = principal?.IsAdmin == true || MapVisibility.Visible(notebook.Regions, point.X, point.Z);
            var targetVisible = principal?.IsAdmin == true || MapVisibility.Visible(notebook.Regions, targetX, targetZ);
            var sourceKey = FormattableString.Invariant($"{point.X},{point.Y},{point.Z}");
            var targetKey = FormattableString.Invariant($"{targetX},{point.TargetY},{targetZ}");
            var pairKey = string.CompareOrdinal(sourceKey, targetKey) <= 0 ? sourceKey + "|" + targetKey : targetKey + "|" + sourceKey;
            if (!emitted.Add(pairKey) || !MapVisibility.TranslocatorVisible(notebook.Regions, point.X, point.Z, targetX, targetZ) || !Intersects(Math.Min(point.X, targetX), Math.Min(point.Z, targetZ), Math.Max(point.X, targetX), Math.Max(point.Z, targetZ), bounds)) continue;

            // Never send the hidden endpoint to a non-admin browser. Keep the
            // feature anchored at the visible endpoint so its marker can still
            // be displayed as "unknown destination" without leaking coordinates.
            var safeSourceX = sourceVisible ? point.X : targetX;
            var safeSourceZ = sourceVisible ? point.Z : targetZ;
            var safeTargetX = targetVisible ? targetX : point.X;
            var safeTargetZ = targetVisible ? targetZ : point.Z;
            var lineVisible = principal?.IsAdmin == true || MapVisibility.TranslocatorLineVisible(notebook.Regions, point.X, point.Z, targetX, targetZ);
            var safeId = principal?.IsAdmin == true || sourceVisible
                ? point.Id
                : $"translocator-{targetX}-{point.TargetY}-{targetZ}";
            features.Add(new { type = "Feature", id = safeId,
                geometry = new { type = "LineString", coordinates = new[] { new[] { safeSourceX, safeSourceZ }, new[] { safeTargetX, safeTargetZ } } },
                properties = new { name = point.Name, y = sourceVisible || principal?.IsAdmin == true ? point.Y : (double?)null,
                    targetY = targetVisible || principal?.IsAdmin == true ? point.TargetY : (double?)null,
                    kind = point.Kind, sourceVisible, targetVisible, lineVisible } });
        }
    }

    private static bool IsMerchantClaim(LandClaim claim)
    {
        var text = $"{claim.Description} {claim.LastKnownOwnerName}";
        return text.Contains("trader", StringComparison.OrdinalIgnoreCase) || text.Contains("merchant", StringComparison.OrdinalIgnoreCase) || text.Contains("商人", StringComparison.OrdinalIgnoreCase);
    }

    private static string ClaimColor(int protectionLevel, int index)
    {
        string[] palette = ["#5ab1d8", "#77c68b", "#d9ad5b", "#c77edb", "#e47b76", "#7f9fe8", "#d58c5d", "#67c8bd"];
        return palette[(Math.Abs(protectionLevel) + index) % palette.Length];
    }

    private static (double MinX, double MinZ, double MaxX, double MaxZ)? ParseBounds(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null; var values = value.Split(',');
        if (values.Length != 4 || !double.TryParse(values[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var minX) || !double.TryParse(values[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var minZ) || !double.TryParse(values[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var maxX) || !double.TryParse(values[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var maxZ)) return null;
        return (Math.Min(minX, maxX), Math.Min(minZ, maxZ), Math.Max(minX, maxX), Math.Max(minZ, maxZ));
    }
    private static bool InBounds(double x, double z, (double MinX, double MinZ, double MaxX, double MaxZ)? b) => b == null || x >= b.Value.MinX && x <= b.Value.MaxX && z >= b.Value.MinZ && z <= b.Value.MaxZ;
    private static bool Intersects(double minX, double minZ, double maxX, double maxZ, (double MinX, double MinZ, double MaxX, double MaxZ)? b) => b == null || minX <= b.Value.MaxX && maxX >= b.Value.MinX && minZ <= b.Value.MaxZ && maxZ >= b.Value.MinZ;
    private static object PointFeature(string id, double x, double z, object properties) => new { type = "Feature", id, geometry = new { type = "Point", coordinates = new[] { x, z } }, properties };
    private string? PlayerAvatar(IPlayer player)
    {
        try
        {
            var fingerprint = PlayerMapSyncSystem.Appearance(player);
            var clientKey = fingerprint == null ? null : ClientAvatars?.GetKey(player.PlayerUID, fingerprint);
            if (clientKey != null) return "api/v1/avatars/" + clientKey + ".png";
            if (avatars == null) return null;
            var parts = player.Entity.WatchedAttributes.GetTreeAttribute("skinConfig")?.GetTreeAttribute("appliedParts");
            if (parts == null) return null;
            var appearance = new LocalAvatarRenderer.Appearance(parts.GetString("baseskin", "skin20"), parts.GetString("eyecolor", "acid-green"),
                parts.GetString("hairbase", "bald"), parts.GetString("hairextra", "none"), parts.GetString("mustache", "none"), parts.GetString("beard", "none"), parts.GetString("haircolor", "cordovan"));
            var key = avatars.Request(appearance);
            return key == null ? null : "api/v1/avatars/" + key + ".png";
        }
        catch { return null; }
    }
    private ConcurrentDictionary<(int X, int Z), byte> LoadBaseTiles(string rendererName)
    {
        var set = new ConcurrentDictionary<(int X, int Z), byte>(); var directory = Path.Combine(root, "2d", rendererName, "0"); if (!Directory.Exists(directory)) return set;
        foreach (var file in Directory.EnumerateFiles(directory, "*.png")) if (TryTileName(Path.GetFileName(file), out var x, out var z)) set.TryAdd((x, z), 0); return set;
    }
    public Action<string, int, int, int, string, bool>? RequestParent { get; set; }
    public string ParentDependencies(string name, int zoom, int x, int z) => pyramid.Dependencies(name, zoom, x, z);
    public void RenderParent(string name, int zoom, int x, int z, string reason, bool rebuild)
    {
        if (pyramid.BuildParent(name, zoom, x, z)) { Interlocked.Increment(ref ParentTiles); events.Publish("tile", new { renderer = name, zoom, x, z }); }
        if (zoom < TilePyramidBuilder.MaxZoom) RequestParent?.Invoke(name, zoom + 1, TilePyramidBuilder.FloorDiv(x, 2), TilePyramidBuilder.FloorDiv(z, 2), reason, rebuild);
    }
    private readonly object objectGate = new();
    private bool indexRepairComplete;
    public bool ObjectIndexRestored => translocators.Restored || indexRepairComplete;
    public void MarkIndexReady() { translocators.Save(); indexRepairComplete = true; }
    public long ExtractedColumns => renderer.ExtractedColumns;
    public long ReusedColumns => renderer.ReusedColumns;
    public long ColoredTiles, ParentTiles, IndexedColumns;
    public int TranslocatorCount => translocators.Values.Length;
    public bool SurfaceExists(ChunkKey key) => File.Exists(SurfaceRegion.PathFor(root, key.X, key.Z));
    public bool SurfaceIsEmpty(ChunkKey key) => surfaceReads.TryGet(key, out var value) && value != null && !value.Columns.Any(c => c);
    public void ForgetRegion(ChunkKey key) => knownRegions.TryRemove((key.X, key.Z), out _);
    public bool SurfaceValid(ChunkKey key) => SurfaceRegion.Load(SurfaceRegion.PathFor(root, key.X, key.Z))?.Width == 512;
    private const int BasicImageVersion = 1, SepiaImageVersion = 1;
    private static string ImagePrefix(string name) => $"surface-{SurfaceRegion.Format}-{name}-image-{(name == "basic" ? BasicImageVersion : SepiaImageVersion)}:";
    private readonly LruCache<ChunkKey, SurfaceRegion?> surfaceReads = new(4);
    private string PatchPath(ChunkKey key, int index) => Path.Combine(root, "surface", $"{key.X}_{key.Z}", $"{index}.br");
    public bool ExtractColumn(ChunkKey key, int index, long target, bool verify, bool repairIndex = false, int[]? objectYs = null, string stateEpoch = "")
    {
        var path = PatchPath(key, index);
        var previous = SurfaceRegion.Load(path);
        var objectVersion = stateEpoch + ":" + (repairIndex || objectYs == null ? "all" : string.Join(",", objectYs.Order()));
        if (previous?.Generation == target && previous.ObjectVersion == objectVersion) return previous.AwaitingSave;
        if (verify && previous == null)
        {
            if (!surfaceReads.TryGet(key, out var region)) { region = SurfaceRegion.Load(SurfaceRegion.PathFor(root, key.X, key.Z)); surfaceReads.Set(key, region); }
            previous = region?.Column(index);
        }
        var fingerprint = previous?.Fingerprints[index];
        var fence = SaveCompletionAdapter.CaptureReadFence();
        using var snapshot = reader.BeginSnapshot();
        SurfaceRegion column;
        try { column = renderer.Extract(key, previous, new HashSet<int> { index }, verify, singleColumn: true); }
        catch (MapRenderer.ColumnGenerationIncompleteException)
        {
            // Partial world generation can persist at the exploration border
            // for days. Preserve old pixels until a real generation event;
            // this state must never be fed back into the save-dirty journal.
            SaveCompletionAdapter.ValidateReadFence(fence);
            var old = SurfaceRegion.Load(SurfaceRegion.PathFor(root, key.X, key.Z))?.Column(index) ?? new SurfaceRegion(32);
            old.Generation = target; old.ObjectVersion = objectVersion; old.AwaitingSave = true; old.Save(path); return true;
        }
        if (repairIndex || !verify || fingerprint != column.Fingerprints[index])
            RefreshObjectColumn(key.X * 16 + index / 16, key.Z * 16 + index % 16, repairIndex ? null : objectYs, () => SaveCompletionAdapter.ValidateReadFence(fence));
        SaveCompletionAdapter.ValidateReadFence(fence);
        column.Generation = target; column.ObjectVersion = objectVersion; column.AwaitingSave = false; column.Save(path); return false;
    }
    public sealed class DamagedColumnException(int index) : IOException("Surface column cache needs repair: " + index) { public int Index => index; }
    public void Render2D(ChunkKey key, bool basicOnly = false)
    {
        var surface = renderer.Extract(key); surface.Generation = DateTime.UtcNow.Ticks;
        surface.Save(SurfaceRegion.PathFor(root, key.X, key.Z));
        RenderCached(key, new RegionWork(surface.Generation, "render", false, false, basicOnly, new(), materials.ClientColormapVersion));
    }
    public bool RenderCached(ChunkKey key, RegionWork work)
    {
        var path = SurfaceRegion.PathFor(root, key.X, key.Z);
        var existing = SurfaceRegion.Load(path);
        var surface = existing ?? new SurfaceRegion();
        var changed = existing == null;
        var patches = new List<string>();
        for (var index = 0; index < 256; index++)
        {
            var patchPath = PatchPath(key, index);
            if (!File.Exists(patchPath)) { if (existing == null) throw new DamagedColumnException(index); continue; }
            var patch = SurfaceRegion.Load(patchPath);
            if (patch?.Width != 32) throw new DamagedColumnException(index);
            changed |= !surface.MatchesColumn(index, patch);
            surface.MergeColumn(index, patch); patches.Add(patchPath);
        }
        if (changed)
        {
            surface.Generation = work.Revision; surface.ContentVersion = Guid.NewGuid().ToString("N"); surface.Save(path);
        }
        // Durable regional replacement precedes patch cleanup and task acknowledgement.
        foreach (var patch in patches) File.Delete(patch);
        surfaceReads.Set(key, surface);
        foreach (var name in work.ColorOnly ? new[] { "basic" } : Renderers)
        {
            var tile = Path.Combine(root, "2d", name, "0", $"{key.X}_{key.Z}.png");
            var stamp = ImagePrefix(name) + surface.ContentVersion + (name == "basic" ? ":" + materials.ClientColormapVersion : "");
            if (work.ForceImages || work.Reason == "render" || !File.Exists(tile) || !File.Exists(tile + ".surface") || File.ReadAllText(tile + ".surface") != stamp || !HasBaseTile(name, key))
            {
                if (!renderer.RenderSurface(key, surface, name)) continue;
                AtomicFile.Replace(tile + ".surface", temp => File.WriteAllText(temp, stamp));
                Interlocked.Increment(ref ColoredTiles);
                baseTiles[name].TryAdd((key.X, key.Z), 0);
                events.Publish("tile", new { renderer = name, zoom = 0, x = key.X, z = key.Z });
            }
            RequestParent?.Invoke(name, 1, TilePyramidBuilder.FloorDiv(key.X, 2), TilePyramidBuilder.FloorDiv(key.Z, 2), work.Reason, work.Rebuild);
        }
        return materials.HasClientColormap;
    }
    public void RefreshObjectColumn(int x, int z, int[]? selectedYs = null, Action? validate = null)
    {
        lock (objectGate)
        {
            var ys = selectedYs ?? reader.ChunkYs(x, z).Concat(translocators.Values.Where(p => (p.X >> 5) == x && (p.Z >> 5) == z).Select(p => p.Y >> 5)).Distinct().ToArray();
            var read = new Dictionary<int, TranslocatorPoint[]>();
            foreach (var y in ys)
            {
                // Null means a successfully queried absent row, not an IO failure.
                var points = reader.ReadTranslocators(new ChunkKey(x, y, z)) ?? [];
                read[y] = points;
            }
            validate?.Invoke();
            var changed = false;
            foreach (var (y, points) in read) changed |= translocators.ReplaceChunk(x, y, z, points);
            translocators.Save(); Interlocked.Increment(ref IndexedColumns);
            if (changed) { var version = layerVersions.AddOrUpdate("translocators", 2, (_, old) => old + 1); events.Publish("layer", new { layer = "translocators", version }); }
        }
    }
    public void NotifyColormapApplied() => events.Publish("colormap", new { month = materials.ClientColormapMonth, version = materials.ClientColormapVersion });
    public IEnumerable<ChunkKey> ExistingRegions => knownRegions.Keys.Select(p => new ChunkKey(p.X, 0, p.Z)).ToArray();
    public void RestoreRegions(IEnumerable<ChunkKey> regions) { knownRegions.Clear(); foreach (var key in regions) NoteRegion(key); }
    public void NoteRegion(ChunkKey key) => knownRegions.TryAdd((key.X, key.Z), 0);
    public IEnumerable<ChunkKey> StaleColorRegions() => knownRegions.Keys.Select(p => new ChunkKey(p.X, 0, p.Z)).Where(key => !HasBaseTile("basic", key));
    public bool HasBaseTile(string rendererName, ChunkKey key)
    {
        if (!baseTiles.TryGetValue(rendererName, out var tiles) || !tiles.ContainsKey((key.X, key.Z))) return false;
        var path = Path.Combine(root, "2d", rendererName, "0", $"{key.X}_{key.Z}.png");
        try
        {
            return TileIntegrity.IsValid(path) && File.ReadAllText(path + ".surface").StartsWith(ImagePrefix(rendererName), StringComparison.Ordinal)
                && (rendererName != "basic" || TileColorStamp.IsCurrent(path, materials.ClientColormapVersion));
        }
        catch (IOException) { return false; }
    }
    public void CheckParents(IEnumerable<ChunkKey> regions, CancellationToken token)
    {
        var seen = new HashSet<(string, int, int, int)>();
        foreach (var key in regions) foreach (var name in Renderers)
        {
            var x = key.X; var z = key.Z;
            if (!File.Exists(Path.Combine(root, "2d", name, "0", $"{x}_{z}.png"))) continue;
            for (var zoom = 1; zoom <= TilePyramidBuilder.MaxZoom; zoom++)
            {
                token.ThrowIfCancellationRequested(); x = TilePyramidBuilder.FloorDiv(x, 2); z = TilePyramidBuilder.FloorDiv(z, 2);
                if (!seen.Add((name, zoom, x, z))) continue;
                if (!pyramid.IsCurrent(name, zoom, x, z)) RequestParent?.Invoke(name, zoom, x, z, "repair", false);
            }
        }
    }
    public void StartBackgroundMaintenance()
    {
        lock (maintenanceGate)
        {
            if (maintenanceStarted || stop.IsCancellationRequested) return;
            maintenanceStarted = true;
            // Rendering, indexing and parents run under the shared RenderThreads budget.
            maintenance = Task.CompletedTask;
        }
    }
    private static string ResolveWebRoot(ICoreServerAPI api)
    {
        var assemblyRoot = Path.GetDirectoryName(typeof(ServerMapWebServer).Assembly.Location); if (!string.IsNullOrWhiteSpace(assemblyRoot) && Directory.Exists(Path.Combine(assemblyRoot, "web"))) return Path.Combine(assemblyRoot, "web");
        var source = api.ModLoader.GetMod("servermap")?.SourcePath; var sourceRoot = string.IsNullOrWhiteSpace(source) ? null : Directory.Exists(source) ? source : Path.GetDirectoryName(source); return string.IsNullOrWhiteSpace(sourceRoot) ? Path.Combine(AppContext.BaseDirectory, "web") : Path.Combine(sourceRoot, "web");
    }
    public Task StopAsync()
    {
        lock (maintenanceGate)
        {
            if (!stop.IsCancellationRequested)
            {
                if (waypointListener != 0) { api.Event.UnregisterGameTickListener(waypointListener); waypointListener = 0; }
                stop.Cancel(); events.Dispose(); avatars?.Dispose(); ClientAvatars?.Dispose();
                try { listener?.Stop(); listener?.Close(); } catch { }
            }
            return maintenance;
        }
    }
    public void Dispose() => _ = StopAsync();
    private sealed record IndexedPoint(string Id, string Name, string Kind, double X, double Y, double Z, double? TargetX = null, double? TargetY = null, double? TargetZ = null);
}
