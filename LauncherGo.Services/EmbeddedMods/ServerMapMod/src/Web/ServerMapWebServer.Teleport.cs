using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace ServerMap.Web;

public sealed partial class ServerMapWebServer
{
    private sealed record TeleportQuote(string Id, string Owner, double X, double Y, double Z, int Cost, bool Admin, DateTimeOffset Expires, PlayerTeleportSettings Settings);
    private sealed record TeleportSnapshot(TeleportRoute.Point Position, bool Admin, int Available, PlayerTeleportSettings Settings, string? EffectError);
    private sealed class TeleportError(int status, string code) : Exception(code) { public int Status { get; } = status; }
    private readonly ConcurrentDictionary<string, TeleportQuote> teleportQuotes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> teleportRequests = new(StringComparer.Ordinal);

    private void HandleTeleport(HttpListenerContext context, bool preview)
    {
        if (context.Request.HttpMethod != "POST") { Error(context, 405, "Method not allowed"); return; }
        var principal = Principal(context.Request);
        if (principal == null) { Error(context, 401, "Login required"); return; }
        if (context.Request.Headers["X-ServerMap-Request"] != "1") { Error(context, 403, "Missing request header"); return; }
        if (!principal.IsAdmin && !announcements.Current.PlayerGearTeleportEnabled) { Error(context, 403, "teleport_disabled"); return; }
        if (!teleportRequests.TryAdd(principal.PlayerUid, 0)) { Error(context, 409, "teleport_busy"); return; }
        try
        {
            using var document = ReadJson(context.Request);
            var input = document.RootElement;
            if (preview)
            {
                var x = input.GetProperty("x").GetDouble(); var z = input.GetProperty("z").GetDouble();
                var accessor = api.World.BlockAccessor;
                if (!double.IsFinite(x) || !double.IsFinite(z) || x < 0 || z < 0 || x >= accessor.MapSizeX || z >= accessor.MapSizeZ)
                    throw new TeleportError(400, "teleport_coordinates");
                x = Math.Floor(x) + .5; z = Math.Floor(z) + .5;
                var snapshot = OnGameThread(() => SnapshotTeleport(context, principal.PlayerUid, x, z));
                var y = reader.SurfaceHeightAt(x, z) ?? throw new TeleportError(404, "teleport_surface");
                var jumps = snapshot.Admin ? 0 : TeleportCost(snapshot.Position, x, y, z);
                var cost = snapshot.Settings.Cost(jumps);
                var reason = !snapshot.Admin && jumps == 0 ? "teleport_zero_jumps" : snapshot.Available < cost ? "teleport_gears" : snapshot.EffectError;
                var quote = new TeleportQuote(Guid.NewGuid().ToString("N"), principal.PlayerUid, x, y, z, cost, snapshot.Admin, DateTimeOffset.UtcNow.AddMinutes(1), snapshot.Settings);
                foreach (var pair in teleportQuotes) if (pair.Value.Expires < DateTimeOffset.UtcNow) teleportQuotes.TryRemove(pair.Key, out _);
                teleportQuotes[principal.PlayerUid] = quote;
                Json(context, new { quoteId = quote.Id, x, y = y + 1, z, jumps, cost, itemCode = snapshot.Settings.ItemCode, settings = snapshot.Settings, available = snapshot.Available, admin = snapshot.Admin, allowed = reason == null, reason }, true);
            }
            else
            {
                var id = input.GetProperty("quoteId").GetString();
                if (!teleportQuotes.TryRemove(principal.PlayerUid, out var quote) || quote.Id != id || quote.Expires < DateTimeOffset.UtcNow)
                    throw new TeleportError(409, "teleport_expired");
                // Recompute on an HTTP worker using the player's current position.
                var snapshot = OnGameThread(() => SnapshotTeleport(context, principal.PlayerUid, quote.X, quote.Z));
                var networkVersion = layerVersions.GetValueOrDefault("translocators");
                var regions = notebook.Regions;
                var cost = snapshot.Admin ? 0 : snapshot.Settings.Cost(TeleportCost(snapshot.Position, quote.X, quote.Y, quote.Z));
                if (snapshot.Admin != quote.Admin || cost != quote.Cost || snapshot.Settings != quote.Settings) throw new TeleportError(409, "teleport_changed");
                if (!snapshot.Admin && cost == 0) throw new TeleportError(409, "teleport_zero_jumps");
                if (snapshot.Available < cost) throw new TeleportError(409, "teleport_gears");
                if (snapshot.EffectError != null) throw new TeleportError(409, snapshot.EffectError);
                // Load first, then validate and commit on the game thread. A late
                // chunk callback cannot charge/teleport after the request times out.
                var result = AtTeleportDestination(quote, () =>
                {
                    var current = SnapshotTeleport(context, principal.PlayerUid, quote.X, quote.Z);
                    if (current.Admin != snapshot.Admin || current.Settings != quote.Settings || !current.Admin && current.Position != snapshot.Position) throw new TeleportError(409, "teleport_changed");
                    if (!current.Admin && (networkVersion != layerVersions.GetValueOrDefault("translocators") || !regions.SequenceEqual(notebook.Regions))) throw new TeleportError(409, "teleport_changed");
                    var player = OnlineTeleportPlayer(principal.PlayerUid);
                    var y = LandingHeight(quote.X, quote.Z);
                    if (!current.Admin && y != quote.Y + 1) throw new TeleportError(409, "teleport_changed");
                    var effects = TeleportEffects.Prepare(player.Entity, current.Admin ? new() : current.Settings);
                    if (effects.Error != null) throw new TeleportError(409, effects.Error);
                    if (!TemporalGearPayment.Execute(TeleportSlots(player), cost, () =>
                    {
                        // EntityPlayer's synchronous completion routine, after
                        // loading the target column. It resets motion/fall state,
                        // broadcasts the teleport and updates chunk subscriptions.
                        player.Entity.Onplrteleported(quote.X, y, quote.Z, null, api);
                    }, current.Settings.ItemCode)) throw new TeleportError(409, "teleport_gears");
                    effects.Apply();
                    api.Logger.Notification("ServerMap teleport: player={0}; target={1},{2},{3}; item={4}; consumed={5}; admin={6}", player.PlayerUID, quote.X, y, quote.Z, current.Settings.ItemCode, cost, current.Admin);
                    return new { ok = true, x = quote.X, y, z = quote.Z, consumed = cost, itemCode = current.Settings.ItemCode };
                });
                Json(context, result, true);
            }
        }
        catch (TeleportError ex) { Error(context, ex.Status, ex.Message); }
        catch (TimeoutException) { Error(context, 503, "teleport_busy"); }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or InvalidDataException or ArgumentException or OverflowException)
        { Error(context, 400, "teleport_invalid"); }
        finally { teleportRequests.TryRemove(principal.PlayerUid, out _); }
    }

    private TeleportSnapshot SnapshotTeleport(HttpListenerContext context, string uid, double x, double z)
    {
        var current = Principal(context.Request);
        if (current?.PlayerUid != uid) throw new TeleportError(401, "Login required");
        if (!current.IsAdmin && !announcements.Current.PlayerGearTeleportEnabled) throw new TeleportError(403, "teleport_disabled");
        if (!CanView(current, x, z)) throw new TeleportError(403, "Hidden region");
        var player = OnlineTeleportPlayer(uid);
        var pos = player.Entity.Pos;
        var settings = (announcements.Current.PlayerTeleport ?? new()).Validate();
        return new(new(pos.X, pos.Y, pos.Z), current.IsAdmin, TemporalGearPayment.Count(TeleportSlots(player), settings.ItemCode), settings,
            current.IsAdmin ? null : TeleportEffects.Prepare(player.Entity, settings).Error);
    }
    private IServerPlayer OnlineTeleportPlayer(string uid)
    {
        var player = api.World.AllOnlinePlayers.OfType<IServerPlayer>().FirstOrDefault(p => p.PlayerUID == uid);
        if (player?.Entity == null) throw new TeleportError(409, "teleport_offline");
        if (!player.Entity.Alive || player.Entity.Teleporting) throw new TeleportError(409, "teleport_busy");
        if (player.Entity.MountedOn != null) throw new TeleportError(409, "teleport_mounted");
        if (player.Entity.Pos.Dimension != 0) throw new TeleportError(409, "teleport_dimension");
        return player;
    }
    private static IEnumerable<ItemSlot> TeleportSlots(IServerPlayer player)
    {
        // Only carried inventories, never an open chest or creative catalogue.
        foreach (var name in new[] { "hotbar", "backpack" })
            if (player.InventoryManager.GetOwnInventory(name) is { } inventory)
                foreach (var slot in inventory) yield return slot;
    }
    private int TeleportCost(TeleportRoute.Point from, double x, double y, double z)
    {
        var regions = notebook.Regions;
        return TeleportRoute.Jumps(translocators.Values.Where(p => MapVisibility.TranslocatorLineVisible(regions, p.X, p.Z, p.TargetX, p.TargetZ)), from, new(x, y, z));
    }
    private double LandingHeight(double x, double z)
    {
        var accessor = api.World.BlockAccessor;
        var y = accessor.GetRainMapHeightAt((int)x, (int)z) + 1;
        if (y < 1 || y + 2 >= accessor.MapSizeY) throw new TeleportError(409, "teleport_surface");
        for (var dy = 0; dy < 2; dy++)
        {
            var position = new BlockPos((int)x, y + dy, (int)z);
            if (accessor.GetBlock(position).GetCollisionBoxes(accessor, position) is { Length: > 0 }) throw new TeleportError(409, "teleport_surface");
        }
        var below = accessor.GetBlock(new BlockPos((int)x, y - 1, (int)z));
        if (below.BlockMaterial == EnumBlockMaterial.Lava) throw new TeleportError(409, "teleport_surface");
        return y;
    }
    private T AtTeleportDestination<T>(TeleportQuote quote, Func<T> action)
    {
        var call = new GameThreadCall<T>(() => { stop.Token.ThrowIfCancellationRequested(); return action(); });
        try
        {
            OnGameThread(() =>
            {
                api.WorldManager.LoadChunkColumnPriority((int)quote.X / 32, (int)quote.Z / 32, new ChunkLoadOptions
                { OnLoaded = () => api.Event.EnqueueMainThreadTask(call.Run, "servermap-teleport") });
                return true;
            });
            return call.Task.WaitAsync(TimeSpan.FromSeconds(30), stop.Token).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            if (call.CancelPending()) throw new TimeoutException();
            return call.Task.GetAwaiter().GetResult();
        }
    }
}
