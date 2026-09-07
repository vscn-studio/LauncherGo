using System.Collections.Concurrent;
using ServerMap.Configuration;
using ServerMap.Network;
using ServerMap.Web;
using ServerMap.World;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.API.Config;
using System.Reflection;

namespace ServerMap;

public sealed class ServerMapModSystem : ModSystem
{
    private const string CacheFormatVersion = "2d-live-flat-5";
    private ICoreServerAPI? sapi; private WorldDatabaseReader? db; private ServerMapWebServer? web; private ServerMapConfig? config; private Render.RenderQueue? queue; private Render.MapPalette? materials; private ClientColormapReceiver? colormapReceiver; private IServerNetworkChannel? colormapChannel; private string dataRoot="", configPath="";
    private readonly ConcurrentDictionary<ChunkKey, byte> regionsAwaitingSave = new();
    private readonly ConcurrentDictionary<ChunkKey, byte> basicOnlyRegions = new();
    private long colormapRequestListenerId;
    private int requestedColormapMonth;
    private long lastColormapRequestAt;
    private bool colormapCacheInitialized;
    private MapAuthStore? authStore;
    private PoiStore? poiStore;
    private readonly DoorStateCache doorStates = new();
    public override void AssetsFinalize(ICoreAPI api) { }
    public override void StartServerSide(ICoreServerAPI api)
    {
        sapi=api; var configDir=api.GetOrCreateDataPath("ServerMap"); dataRoot=Path.Combine(configDir,api.World.SavegameIdentifier); configPath=Path.Combine(configDir,"servermap.json"); Directory.CreateDirectory(dataRoot);
        config ??= ServerMapConfig.Load(configPath,api.Logger.Error);
        materials ??= Render.MapPalette.Capture(api);
        EnsureCacheFormat();
        try { db=new WorldDatabaseReader(api,config.ChunkCacheSize); authStore=new MapAuthStore(Path.Combine(dataRoot,"auth-accounts.json")); poiStore=new PoiStore(Path.Combine(dataRoot,"pois.json")); var announcementStore=new AnnouncementStore(Path.Combine(dataRoot,"announcement.json")); web=new ServerMapWebServer(api,config,dataRoot,db,materials,authStore,poiStore,announcementStore); queue=new Render.RenderQueue(config.RenderThreads, RenderRegion); colormapReceiver=new ClientColormapReceiver(api,materials,OnClientColormapApplied,Path.Combine(dataRoot,"colormap")); colormapChannel=api.Network.RegisterChannel("servermap-colormap").RegisterMessageType<ServerColormapRequestPacket>().RegisterMessageType<ClientColormapChunkPacket>().SetMessageHandler<ClientColormapChunkPacket>(colormapReceiver.Receive); web.Start(); api.Event.ChunkDirty += OnChunkDirty; api.Event.ChunkColumnLoaded += OnChunkColumnLoaded; api.Event.GameWorldSave += OnSave; api.Event.PlayerNowPlaying += OnPlayerNowPlaying; api.Event.ServerRunPhase(EnumServerRunPhase.GameReady, InitializeColormapCache); colormapRequestListenerId=api.Event.RegisterGameTickListener(_ => RequestMissingColormap(),30000); api.RegisterCommand("servermap", "ServerMap commands", "/servermap status|reload|psw <password>|render all", OnCommand, "chat"); api.Event.RegisterCallback(_ => { InitializeColormapCache(); QueueExistingRegions(); RequestMissingColormap(); }, 1000); api.Logger.Notification("ServerMap 2D-only initialized; client colormap cache will load at GameReady."); }
        catch(Exception ex){api.Logger.Error("ServerMap initialization failed: {0}",ex);}
    }
    private void OnChunkDirty(Vec3i chunkPos, IWorldChunk chunk, EnumChunkDirtyReason reason)
    {
        QueueAfterSave(new ChunkKey(chunkPos.X >> 4, 0, chunkPos.Z >> 4));
    }
    private void OnChunkColumnLoaded(Vec2i chunkPos, IWorldChunk[] chunks)
    {
        QueueAfterSave(new ChunkKey(chunkPos.X >> 4, 0, chunkPos.Y >> 4));
    }
    private void QueueAfterSave(ChunkKey region)
    {
        // WorldDatabaseReader intentionally reads the persisted SQLite world.
        // Rendering now would see the previous save (or half-written chunks),
        // so mirror LiveMap: remember the region and process it after save.
        db?.InvalidateChunkIndex();
        regionsAwaitingSave.TryAdd(region, 0);
    }
    private void QueueExistingRegions()
    {
        if (db == null) return;
        foreach (var region in db.MapColumns().Select(column => new ChunkKey(column.X >> 4, 0, column.Z >> 4)).Distinct())
            if (web?.HasBaseTile("basic", region) != true || web.HasBaseTile("sepia", region) != true) queue?.Enqueue(region);
    }
    private void QueueExistingBasicRegions()
    {
        if (db == null) return;
        var regions = db.MapColumns().Select(column => new ChunkKey(column.X >> 4, 0, column.Z >> 4)).Distinct().ToArray();
        foreach (var region in regions)
        {
            // A new world still needs its sepia base tile. Only narrow the
            // redraw when the non-colour tile already exists.
            if (web?.HasBaseTile("sepia", region) == true) basicOnlyRegions[region] = 0;
            queue?.Enqueue(region);
        }
        sapi?.Logger.Notification("ServerMap queued {0} regions for client-color basic redraw.", regions.Length);
    }
    private void OnClientColormapApplied()
    {
        requestedColormapMonth = 0;
        web?.NotifyColormapApplied();
        QueueExistingBasicRegions();
    }
    private void OnPlayerNowPlaying(IServerPlayer player) => RequestMissingColormap(player);
    private void InitializeColormapCache()
    {
        if (colormapCacheInitialized || sapi?.World.Calendar == null || materials == null) return;
        colormapCacheInitialized = true;
        web?.StartObjectIndex();
        var month = Math.Clamp(sapi.World.Calendar.Month, 1, 12);
        var loaded = materials.LoadClientColormap(Path.Combine(dataRoot, "colormap"), month, message => sapi.Logger.Notification(message));
        sapi.Logger.Notification("ServerMap client colormap cache: month={0}, loaded={1}.", month, loaded);
        if (loaded) web?.NotifyColormapApplied();
    }
    private void RequestMissingColormap(IServerPlayer? candidate = null)
    {
        if (sapi == null || sapi.World.Calendar == null || materials == null || colormapChannel == null) return;
        InitializeColormapCache();
        var month = Math.Clamp(sapi.World.Calendar.Month, 1, 12);
        if (materials.HasClientColormap && materials.ClientColormapMonth == month) return;
        var player = candidate is { } && candidate.HasPrivilege(Privilege.root)
            ? candidate : sapi.World.AllOnlinePlayers.OfType<IServerPlayer>().FirstOrDefault(value => value.HasPrivilege(Privilege.root));
        if (player == null) return;
        var now = Environment.TickCount64;
        if (requestedColormapMonth == month && now - lastColormapRequestAt < 120000) return;
        requestedColormapMonth = month; lastColormapRequestAt = now;
        colormapChannel.SendPacket(new ServerColormapRequestPacket { Month = month }, player);
        sapi.Logger.Notification("ServerMap requested client colormap month {0} from {1}.", month, player.PlayerName);
    }
    private Render.RenderQueueOutcome RenderRegion(ChunkKey job)
    {
        try
        {
            if (config?.Enable2D == true) web?.Render2D(job, basicOnlyRegions.TryRemove(job, out _));
            return Render.RenderQueueOutcome.Completed;
        }
        catch(Exception ex)
        {
            sapi?.Logger.Warning("ServerMap render failed: {0}",ex.ToString());
            return Render.RenderQueueOutcome.RetryLater;
        }
    }
    private void OnSave()
    {
        db?.Clear();
        // The game save event fires before all SQLite writes are guaranteed
        // visible to a second read-only connection.  LiveMap uses the same
        // short delay before it scans queued regions.
        sapi?.Event.RegisterCallback(_ => FlushSavedRegions(), 1000);
    }
    private void FlushSavedRegions()
    {
        foreach (var region in regionsAwaitingSave.Keys)
            if (regionsAwaitingSave.TryRemove(region, out _)) queue?.Enqueue(region, priority: true);
    }
    private void OnCommand(IServerPlayer player, int groupId, CmdArgs args)
    {
        // ChatCommandApi may invoke handlers with no argument object for a
        // bare command (for example `/servermap`). Keep status/help paths
        // safe instead of dereferencing a null CmdArgs instance.
        args ??= new CmdArgs();
        var command = args.PopWord();
        if (command == "reload")
        {
            if (player != null && !player.HasPrivilege(Privilege.root)) { Reply(player, groupId, "ServerMap reload requires the root privilege."); return; }
            if (config == null || string.IsNullOrWhiteSpace(configPath)) { Reply(player, groupId, "ServerMap configuration is not initialized."); return; }
            var oldBind = config.BindAddress; var oldPort = config.Port; var oldEnabled = config.Enabled; var oldThreads = config.RenderThreads; var oldCache = config.ChunkCacheSize;
            if (!ServerMapConfig.TryReload(configPath, config, out var error)) { Reply(player, groupId, "ServerMap config reload failed: " + error); return; }
            var restartRequired = oldBind != config.BindAddress || oldPort != config.Port || oldEnabled != config.Enabled || oldThreads != config.RenderThreads || oldCache != config.ChunkCacheSize;
            Reply(player, groupId, restartRequired ? "ServerMap config reloaded. BindAddress, Port, Enabled, RenderThreads or ChunkCacheSize changes require a server restart." : "ServerMap config reloaded and applied.");
            return;
        }
        if (command == "psw" && player != null && authStore != null)
        {
            var password = args.PopWord() ?? "";
            Reply(player, groupId, authStore.SetPassword(player, password) ? "ServerMap password saved. You can now log in with your player name and password." : "Password must be at least 6 characters.");
            return;
        }
        if (command == "render" && db != null)
        {
            if (player != null && !player.HasPrivilege("root")) { Reply(player, groupId, "ServerMap render requires the root privilege."); return; }
            foreach(var column in db.MapColumns()) queue?.Enqueue(new ChunkKey(column.X >> 4, 0, column.Z >> 4));
            Reply(player, groupId, "ServerMap 2D full render queued."); return;
        }
        Reply(player, groupId, $"ServerMap 2D is running. Web: {config?.BindAddress}:{config?.Port}; queued: {queue?.PendingCount ?? 0}; client colormap: {materials?.HasClientColormap} (month {materials?.ClientColormapMonth})");
    }
    private static int FloorDiv(int value, int divisor) => value >= 0 ? value / divisor : (value - divisor + 1) / divisor;
    private void Reply(IServerPlayer? player, int groupId, string message)
    {
        if (player != null)
        {
            player.SendMessage(groupId, message, EnumChatType.CommandSuccess);
            return;
        }
        sapi?.Logger.Notification("ServerMap: {0}", message);
    }
    public override void Dispose(){if(sapi!=null){sapi.Event.ChunkDirty-=OnChunkDirty;sapi.Event.ChunkColumnLoaded-=OnChunkColumnLoaded;sapi.Event.GameWorldSave-=OnSave;sapi.Event.PlayerNowPlaying-=OnPlayerNowPlaying;if(colormapRequestListenerId!=0)sapi.Event.UnregisterGameTickListener(colormapRequestListenerId);}colormapReceiver?.Dispose();colormapReceiver=null;colormapChannel=null;queue?.Dispose();web?.Dispose();db?.Dispose();base.Dispose();}
    private void EnsureCacheFormat()
    {
        var marker = Path.Combine(dataRoot, "cache-format.txt");
        var expected = CacheFormatVersion;
        sapi?.Logger.Notification("ServerMap cache-format={0}; marker={1}; expected={2}", CacheFormatVersion, marker, expected);
        if (File.Exists(marker) && File.ReadAllText(marker).Trim() == expected)
        {
            sapi?.Logger.Notification("ServerMap cache-format={0} already current.", CacheFormatVersion);
            return;
        }

        foreach (var directory in new[] { Path.Combine(dataRoot, "2d") })
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
        File.WriteAllText(marker, expected);
        sapi?.Logger.Notification("ServerMap cleared obsolete 2D map cache; cache-format={0}.", CacheFormatVersion);
    }
    private void ClearGeneratedMapCache()
    {
        foreach (var directory in new[] { Path.Combine(dataRoot, "2d") })
        {
            try
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
            catch (Exception ex)
            {
                sapi?.Logger.Warning("ServerMap could not clear generated cache {0}: {1}", directory, ex.Message);
            }
        }
        Directory.CreateDirectory(Path.Combine(dataRoot, "2d"));
    }
    private static T? Field<T>(object value,string name) where T:class => value.GetType().GetField(name,BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance)?.GetValue(value) as T;
}
