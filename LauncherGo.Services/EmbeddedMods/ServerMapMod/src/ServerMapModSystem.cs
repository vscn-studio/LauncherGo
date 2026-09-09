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
using ServerMap.Util;
using ServerMap.Render;
using System.Diagnostics;

namespace ServerMap;

public sealed class ServerMapModSystem : ModSystem
{
    public ServerMapWebServer? WebServer => web;
    private const string CacheFormatVersion = "2d-live-flat-5";
    private ICoreServerAPI? sapi; private WorldDatabaseReader? db; private ServerMapWebServer? web; private ServerMapConfig? config; private Render.RenderQueue? queue; private Render.MapPalette? materials; private ClientColormapReceiver? colormapReceiver; private IServerNetworkChannel? colormapChannel; private string dataRoot="", configPath="";
    private MapCacheState? cache;
    private Dictionary<string, long> restoredDirty = new();
    private SaveCompletionAdapter? saveAdapter;
    private long saveListenerId;
    private string? lastError;
    private readonly ConcurrentDictionary<ChunkKey, byte> waitingForSave = new();
    private readonly ConcurrentDictionary<string, string> taskErrors = new();
    private string scanReason = "startup";
    private long colormapRequestListenerId;
    private int requestedColormapMonth;
    private long lastColormapRequestAt;
    private bool colormapCacheInitialized;
    private BackgroundMapWork? background;
    private volatile bool running;
    private volatile bool disposed;
    private MapAuthStore? authStore;
    private PoiStore? poiStore;
    private int scanActive, scannedColumns;
    private readonly DoorStateCache doorStates = new();
    public override void AssetsFinalize(ICoreAPI api) { }
    public override void StartServerSide(ICoreServerAPI api)
    {
        sapi=api; var configDir=api.GetOrCreateDataPath("ServerMap"); dataRoot=Path.Combine(configDir,api.World.SavegameIdentifier); configPath=Path.Combine(configDir,"servermap.json"); Directory.CreateDirectory(dataRoot);
        config ??= ServerMapConfig.Load(configPath,api.Logger.Error);
        if (!config.Enabled) { api.Logger.Notification("ServerMap disabled; no database or background scans started."); return; }
        materials ??= Render.MapPalette.Capture(api);
        EnsureCacheFormat();
        try
        {
            cache = new MapCacheState(Path.Combine(dataRoot, "cache-state.db"));
            if (cache.RecoveryNotice != null) api.Logger.Warning("ServerMap: {0}", cache.RecoveryNotice);
            restoredDirty = cache.Freeze();
            db = new WorldDatabaseReader(api, config.ChunkCacheSize);
            authStore = new MapAuthStore(Path.Combine(dataRoot, "auth-accounts.json"));
            poiStore = new PoiStore(Path.Combine(dataRoot, "pois.json"));
            var announcementStore = new AnnouncementStore(Path.Combine(dataRoot, "announcement.json"));
            web = new ServerMapWebServer(api, config, dataRoot, db, materials, authStore, poiStore, announcementStore);
            queue = new Render.RenderQueue(config.RenderThreads, RenderRegion);
            web.RequestParent = RequestParent;
            web.RenderProgress = () =>
            {
                var progress = queue.Progress;
                var tasks = cache.Pending.Values.ToArray();
                var rebuilding = cache.Get("rebuilding") == "yes";
                var reason = Volatile.Read(ref scanActive) != 0 ? scanReason : tasks.Any(w => w.Reason == "changes") ? "changes" : tasks.FirstOrDefault()?.Reason ?? (cache.AwaitingSave > 0 ? "changes" : "idle");
                return new { phase = !running ? "waiting" : Volatile.Read(ref scanActive) != 0 ? "scanning" : progress.Active + progress.Queued > 0 ? "rendering" : progress.Retrying > 0 ? "retrying" : waitingForSave.Count > 0 || cache.AwaitingSave > 0 ? "waiting-save" : tasks.Length > 0 && materials?.HasClientColormap != true ? "waiting-colormap" : "idle",
                    queued = progress.Queued, active = progress.Active, retrying = progress.Retrying, completed = progress.Completed, failed = progress.Failed,
                    columnsScanned = Volatile.Read(ref scannedColumns), regionsDiscovered = cache.Regions.Length, awaitingSave = cache.AwaitingSave, waitingSaveTasks = waitingForSave.Count, lastCompletedAt = progress.LastCompletedAt,
                    reason, rebuilding, rebuildId = cache.Get("rebuildId") ?? "", surfaceExtraction = web.ExtractedColumns, cacheReused = web.ReusedColumns, coloring = web.ColoredTiles, parents = web.ParentTiles, indexing = web.IndexedColumns,
                    mapColumnReads = db.MapColumnReads, chunkReads = db.ChunkDataReads, chunkDeserializations = db.ChunkDeserializations,
                    merged = cache.Merged, pending = tasks.Length, error = cache.Error ?? lastError ?? taskErrors.Values.FirstOrDefault(), cacheProtocol = 1 };
            };
            background = new BackgroundMapWork((job, ex) => api.Logger.Warning("ServerMap background {0} failed: {1}", job, ex));
            colormapReceiver = new ClientColormapReceiver(api, materials, OnClientColormapApplied, Path.Combine(dataRoot, "colormap"));
            colormapChannel = api.Network.RegisterChannel("servermap-colormap")
                .RegisterMessageType<ServerColormapRequestPacket>().RegisterMessageType<ClientColormapChunkPacket>()
                .SetMessageHandler<ClientColormapChunkPacket>(colormapReceiver.Receive)
                .RegisterMessageType<ClientWaypointIconPacket>()
                .SetMessageHandler<ClientWaypointIconPacket>(web.ReceiveWaypointIcon);
            web.Start();
            api.Event.ChunkDirty += OnChunkDirty;
            api.Event.ChunkColumnLoaded += OnChunkColumnLoaded;
            try { saveAdapter = new SaveCompletionAdapter(api, FreezeSave, message => { lastError = message; api.Logger.Warning("ServerMap: {0}", message); }); }
            catch (Exception ex) { lastError = "Save adapter unavailable; changes remain pending: " + ex.Message; api.Logger.Warning("ServerMap: {0}", lastError); }
            saveListenerId = api.Event.RegisterGameTickListener(_ => saveAdapter?.Tick(), 100);
            api.Event.PlayerNowPlaying += OnPlayerNowPlaying;
            api.Event.ServerRunPhase(EnumServerRunPhase.GameReady, InitializeColormapCache);
            api.Event.ServerRunPhase(EnumServerRunPhase.RunGame, StartBackgroundWork);
            colormapRequestListenerId = api.Event.RegisterGameTickListener(_ => RequestMissingColormap(), 30000);
            api.RegisterCommand("servermap", "ServerMap commands", "/servermap status|reload|psw <password>|render all|cache rebuild", OnCommand, "chat");
            api.Logger.Notification("ServerMap initialized; database scans deferred until RunGame.");
        }
        catch(Exception ex){api.Logger.Error("ServerMap initialization failed: {0}",ex);}
    }
    private static string RegionId(ChunkKey key) => key.Y == 0 ? $"{key.X}_{key.Z}" : $"p_{key.X}_{key.Y}_{key.Z}";
    private static ChunkKey RegionKey(string id) { var p = id.Split('_'); return p.Length == 2 ? new(int.Parse(p[0]), 0, int.Parse(p[1])) : new(int.Parse(p[1]), int.Parse(p[2]), int.Parse(p[3])); }
    private void OnChunkDirty(Vec3i chunkPos, IWorldChunk chunk, EnumChunkDirtyReason reason)
    {
        if (disposed || cache == null) return;
        if (reason == EnumChunkDirtyReason.NewlyLoaded) return;
        cache.MarkDirty($"{chunkPos.X}_{chunkPos.Y}_{chunkPos.Z}");
    }
    private void OnChunkColumnLoaded(Vec2i chunkPos, IWorldChunk[] chunks)
    {
        // Loading a column is ordinary read activity. It must not enter the
        // save wait queue; actual mutations arrive through ChunkDirty.
        // Newly discovered columns are picked up by the persisted region scan.
    }
    private void Request(ChunkKey region, string reason, bool extract = false, bool verify = false, bool colorOnly = false, Dictionary<int, long>? columns = null, Dictionary<int, int[]>? objectYs = null)
    {
        if (disposed || cache == null) return;
        web?.NoteRegion(region);
        var task = cache.Request(RegionId(region), reason, extract, verify, colorOnly, columns, materials?.ClientColormapVersion ?? "fallback", objectYs);
        queue?.Enqueue(region, task.Reason == "changes", task.Revision, task.Reason == "season");
    }
    private void RequestParent(string name, int zoom, int x, int z, string reason, bool rebuild)
    {
        if (disposed || cache == null || web == null) return;
        var key = new ChunkKey(x, zoom + (name == "sepia" ? TilePyramidBuilder.MaxZoom : 0), z);
        var task = cache.RequestParent(RegionId(key), reason, web.ParentDependencies(name, zoom, x, z), rebuild);
        queue?.Enqueue(key, task.Reason == "changes", task.Revision, task.Reason == "season");
    }
    private void StartBackgroundWork()
    {
        if (disposed || running) return;
        running = true;
        background?.Enqueue("startup", async token =>
        {
            web?.StartBackgroundMaintenance();
            if (cache!.Get("initialized") != "yes") foreach (var key in web?.ExistingRegions ?? []) cache.NoteRegion(RegionId(key));
            web?.RestoreRegions(cache.Regions.Select(RegionKey));
            QueueSaved(restoredDirty); restoredDirty.Clear();
            foreach (var (id, task) in cache.Pending) queue?.Enqueue(RegionKey(id), task.Reason == "changes", task.Revision, task.Reason == "season");
            var scan = cache.Get("scan");
            if (scan != null && scan != "done")
            {
                await ScanRegionsAsync(scan, true, scan == "recovery", token);
                if (cache.RecoveryRequired && scan != "recovery") await ScanRegionsAsync("recovery", true, true, token);
            }
            else if (cache.Get("initialized") != "yes") await ScanRegionsAsync("build", true, false, token);
            else if (cache.RecoveryRequired || web?.ObjectIndexRestored != true) await ScanRegionsAsync("recovery", true, true, token);
            else
            {
                // Read only derived cache files. A normal restart performs no world SQL scan.
                foreach (var id in cache.Regions)
                {
                    token.ThrowIfCancellationRequested(); var key = RegionKey(id);
                    if (!web!.SurfaceValid(key)) Request(key, "repair", true);
                    else if (!web.HasBaseTile("sepia", key)) Request(key, "repair");
                    else if (materials!.HasClientColormap && !web.HasBaseTile("basic", key)) Request(key, "season", colorOnly: true);
                }
                web?.CheckParents(cache.Regions.Select(RegionKey), token);
                sapi?.Logger.Notification("ServerMap restored region index: {0}; world scans=0.", cache.Regions.Length);
            }
            await ExecuteRequestedRebuild(token);
            FinishRebuild();
        });
        background?.Start(TimeSpan.FromSeconds(5));
    }
    private Task ExecuteRequestedRebuild(CancellationToken token)
    {
        if (cache?.Get("requestedScan") != "rebuild") return Task.CompletedTask;
        db?.Clear();
        return ScanRegionsAsync("rebuild", true, false, token);
    }
    private async Task ScanRegionsAsync(string reason, bool extract, bool verify, CancellationToken token)
    {
        if (db == null || cache == null) return;
        scanReason = reason;
        Interlocked.Exchange(ref scanActive, 1); Interlocked.Exchange(ref scannedColumns, 0);
        var watch = Stopwatch.StartNew();
        if (cache.Get("scan") != reason) { cache.Set("scan", reason); cache.Set("scanAfter", ""); cache.Set("scanId", Guid.NewGuid().ToString("N")); }
        if (reason == "rebuild") cache.Set("requestedScan", "");
        var scanId = cache.Get("scanId") ?? "first";
        var after = long.TryParse(cache.Get("scanAfter"), out var position) ? position : (long?)null;
        try
        {
            sapi?.Logger.Notification("ServerMap scan started: reason={0}; resume={1}.", reason, after);
            foreach (var column in db.MapColumns(token, after))
            {
                token.ThrowIfCancellationRequested();
                var region = new ChunkKey(column.X >> 4, 0, column.Z >> 4); var id = RegionId(region);
                cache.Set($"column:{column.X}_{column.Z}", "yes");
                if (cache.Get("scanRegion:" + id) != scanId)
                {
                    Request(region, reason, extract, verify);
                    cache.Set("scanRegion:" + id, scanId);
                }
                cache.Set("scanAfter", column.ToIndex().ToString());
                if (Interlocked.Increment(ref scannedColumns) % 256 == 0) await Task.Delay(1, token).ConfigureAwait(false);
            }
            foreach (var id in cache.Regions)
                if (cache.Get("scanRegion:" + id) != scanId) Request(RegionKey(id), reason, true, verify);
            cache.Set("initialized", "yes"); cache.Set("scan", "done");
            sapi?.Logger.Notification("ServerMap scan completed: reason={0}; columns={1}; regions={2}; elapsed-ms={3}.", reason, scannedColumns, cache.Regions.Length, watch.ElapsedMilliseconds);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { lastError = ex.Message; throw; }
        finally { Interlocked.Exchange(ref scanActive, 0); FinishRebuild(); }
    }
    private void OnClientColormapApplied()
    {
        requestedColormapMonth = 0;
        web?.NotifyColormapApplied();
        background?.Enqueue("colormap-redraw", token =>
        {
            if (materials?.HasClientColormap == true && cache != null)
                foreach (var id in cache.Regions)
                {
                    token.ThrowIfCancellationRequested(); var key = RegionKey(id);
                    if (web?.HasBaseTile("basic", key) != true) Request(key, "season", colorOnly: true);
                }
            return Task.CompletedTask;
        });
    }
    private void OnPlayerNowPlaying(IServerPlayer player) => RequestMissingColormap(player);
    private void InitializeColormapCache()
    {
        if (disposed || colormapCacheInitialized || sapi?.World.Calendar == null || materials == null) return;
        materials.InitializeRoofing();
        if (materials.Roofing is { } roofing)
            sapi.Logger.Notification("ServerMap roofing definitions ready: roofs={0}; frames={1}.", roofing.RoofCount, roofing.FrameCount);
        colormapCacheInitialized = true;
        var month = Math.Clamp(sapi.World.Calendar.Month, 1, 12);
        var loaded = materials.LoadClientColormap(Path.Combine(dataRoot, "colormap"), month, message => sapi.Logger.Notification(message));
        sapi.Logger.Notification("ServerMap client colormap cache: month={0}, loaded={1}.", month, loaded);
        if (loaded) OnClientColormapApplied();
    }
    private void RequestMissingColormap(IServerPlayer? candidate = null)
    {
        if (disposed || !running || sapi == null || sapi.World.Calendar == null || materials == null || colormapChannel == null) return;
        InitializeColormapCache();
        var month = Math.Clamp(sapi.World.Calendar.Month, 1, 12);
        if (materials.HasClientColormap && materials.ClientColormapMonth == month && materials.HasRoofingColormap && materials.HasGroundStorageColormap) return;
        if (materials.ClientColormapMonth != month && materials.LoadClientColormap(Path.Combine(dataRoot, "colormap"), month, message => sapi.Logger.Notification(message)))
        {
            OnClientColormapApplied();
            if (materials.HasRoofingColormap && materials.HasGroundStorageColormap) return;
        }
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
        if (disposed || cache == null) return Render.RenderQueueOutcome.Completed;
        var task = cache.Find(RegionId(job)); if (task == null) return Render.RenderQueueOutcome.Completed;
        var watch = Stopwatch.StartNew();
        try
        {
            if (job.Y > 0)
            {
                var name = job.Y > TilePyramidBuilder.MaxZoom ? "sepia" : "basic";
                var zoom = (job.Y - 1) % TilePyramidBuilder.MaxZoom + 1;
                web?.RenderParent(name, zoom, job.X, job.Z, task.Reason, task.Rebuild);
                cache.Complete(RegionId(job), task.Revision); taskErrors.TryRemove(RegionId(job), out _); FinishRebuild();
                return Render.RenderQueueOutcome.Completed;
            }
            if (task.Columns.Count > 0)
            {
                if (saveAdapter == null) return Render.RenderQueueOutcome.RetryLater;
                if (SaveCompletionAdapter.SaveInProgress)
                {
                    waitingForSave[job] = 0;
                    if (SaveCompletionAdapter.SaveInProgress) return Render.RenderQueueOutcome.WaitForSignal;
                    waitingForSave.TryRemove(job, out _); return Render.RenderQueueOutcome.Yield;
                }
                var column = task.Columns.First();
                if (web?.ExtractColumn(job, column.Key, column.Value, task.Verify, web.ObjectIndexRestored != true, task.ObjectYs.GetValueOrDefault(column.Key), cache.Epoch) == true)
                    cache.MarkDirty($"{job.X * 16 + column.Key / 16}_0_{job.Z * 16 + column.Key % 16}");
                cache.CompleteColumn(RegionId(job), column.Key, column.Value, task.Revision);
                taskErrors.TryRemove(RegionId(job), out _);
                return Render.RenderQueueOutcome.Yield;
            }
            if (config?.Enable2D == true && web?.RenderCached(job, task) == false) { FinishRebuild(); return Render.RenderQueueOutcome.WaitForSignal; }
            cache.Complete(RegionId(job), task.Revision);
            taskErrors.TryRemove(RegionId(job), out _);
            if (web?.SurfaceIsEmpty(job) == true && cache.Find(RegionId(job)) == null) { cache.RemoveRegion(RegionId(job)); web.ForgetRegion(job); }
            FinishRebuild();
            sapi?.Logger.Notification("ServerMap task {0}: reason={1}; generation={2}; elapsed-ms={3}; extracted-total={4}; reused-total={5}; merged={6}; chunk-reads={7}; deserializations={8}.", job, task.Reason, task.Revision, watch.ElapsedMilliseconds, web?.ExtractedColumns, web?.ReusedColumns, cache.Merged, db?.ChunkDataReads, db?.ChunkDeserializations);
            return Render.RenderQueueOutcome.Completed;
        }
        catch (ServerMapWebServer.DamagedColumnException ex)
        {
            Request(job, "repair", columns: new Dictionary<int, long> { [ex.Index] = task.Revision + 1 });
            return Render.RenderQueueOutcome.Yield;
        }
        catch (OperationCanceledException) when (disposed) { return Render.RenderQueueOutcome.Completed; }
        catch (Exception ex) { taskErrors[RegionId(job)] = ex.Message; sapi?.Logger.Warning("ServerMap render failed: {0}", ex); return Render.RenderQueueOutcome.RetryLater; }
    }
    private void FinishRebuild()
    {
        if (cache?.Get("scan") == "done" && !cache.HasExtractionWork) web?.MarkIndexReady();
        if (cache?.Get("rebuilding") == "yes" && cache.Get("scan") == "done" && cache.Get("requestedScan") != "rebuild" && !cache.HasRebuildWork) cache.Set("rebuilding", "no");
    }
    private Action FreezeSave()
    {
        var frozen = cache?.Freeze() ?? new Dictionary<string, long>();
        return () => QueueSaved(frozen);
    }
    private void QueueSaved(Dictionary<string, long> frozen)
    {
        if (disposed || cache == null) return;
        db?.Clear(); if (saveAdapter != null) lastError = null;
        foreach (var group in frozen.GroupBy(pair => { var p = pair.Key.Split('_'); return new ChunkKey(int.Parse(p[0]) >> 4, 0, int.Parse(p[2]) >> 4); }))
        {
            var columns = new Dictionary<int, long>();
            var objectYs = new Dictionary<int, HashSet<int>>();
            foreach (var (id, version) in group)
            {
                var p = id.Split('_'); var x = int.Parse(p[0]); var z = int.Parse(p[2]);
                var index = (x & 15) * 16 + (z & 15); columns[index] = Math.Max(columns.GetValueOrDefault(index), version);
                if (!objectYs.TryGetValue(index, out var ys)) objectYs[index] = ys = new();
                ys.Add(int.Parse(p[1]));
                cache.Set($"column:{x}_{z}", "yes");
            }
            // Journal the render before acknowledging any saved dirty entry.
            Request(group.Key, "changes", columns: columns, objectYs: objectYs.ToDictionary(p => p.Key, p => p.Value.ToArray()));
            foreach (var (id, version) in group) cache.ConfirmSaved(id, version);
        }
        foreach (var key in waitingForSave.Keys)
            if (waitingForSave.TryRemove(key, out _) && cache.Find(RegionId(key)) is { } task)
                queue?.Enqueue(key, task.Reason == "changes", version: 0, seasonal: task.Reason == "season");
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
        if (command == "cache" && args.PopWord() == "rebuild")
        {
            if (player != null && !player.HasPrivilege(Privilege.root)) { Reply(player, groupId, "ServerMap cache rebuild requires the root privilege."); return; }
            if (!running || cache == null) { Reply(player, groupId, "ServerMap is not ready."); return; }
            if (cache.Get("rebuilding") == "yes") { Reply(player, groupId, "ServerMap cache rebuild already in progress."); return; }
            cache.Set("rebuilding", "yes"); cache.Set("rebuildId", Guid.NewGuid().ToString("N")); lastError = null;
            cache.Set("requestedScan", "rebuild");
            background?.Enqueue("cache-rebuild", ExecuteRequestedRebuild);
            Reply(player, groupId, "ServerMap cache rebuild queued; existing maps remain available."); return;
        }
        if (command == "render" && db != null)
        {
            if (player != null && !player.HasPrivilege("root")) { Reply(player, groupId, "ServerMap render requires the root privilege."); return; }
            background?.Enqueue("full-render", token =>
            {
                foreach (var id in cache?.Regions ?? []) Request(RegionKey(id), "render");
                return Task.CompletedTask;
            });
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
    public override void Dispose()
    {
        if (disposed) return;
        saveAdapter?.Tick();
        disposed = true;
        if (sapi != null)
        {
            sapi.Event.ChunkDirty -= OnChunkDirty;
            sapi.Event.ChunkColumnLoaded -= OnChunkColumnLoaded;
            if (saveListenerId != 0) sapi.Event.UnregisterGameTickListener(saveListenerId);
            sapi.Event.PlayerNowPlaying -= OnPlayerNowPlaying;
            if (colormapRequestListenerId != 0) sapi.Event.UnregisterGameTickListener(colormapRequestListenerId);
        }
        saveAdapter?.Dispose();
        cache?.Close(clean: true);
        colormapReceiver?.Dispose();
        colormapReceiver = null; colormapChannel = null;
        db?.RequestStop();
        var scans = background?.StopAsync() ?? Task.CompletedTask;
        var maintenance = web?.StopAsync() ?? Task.CompletedTask;
        var renders = queue?.StopAsync() ?? Task.CompletedTask;
        // Never make the game/shutdown thread wait for a map SQL operation.
        _ = Task.Run(async () =>
        {
            try { await Task.WhenAll(scans, maintenance, renders).ConfigureAwait(false); }
            catch (Exception ex) { sapi?.Logger.Warning("ServerMap background shutdown: {0}", ex.Message); }
            finally { db?.Dispose(); }
        });
        base.Dispose();
    }
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

        File.WriteAllText(marker, expected);
        sapi?.Logger.Notification("ServerMap cache format changed; existing images are retained during derived-cache migration.");
    }
    private static T? Field<T>(object value,string name) where T:class => value.GetType().GetField(name,BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance)?.GetValue(value) as T;
}
