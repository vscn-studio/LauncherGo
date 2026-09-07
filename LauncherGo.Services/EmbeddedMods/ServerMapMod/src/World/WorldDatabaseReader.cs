using Microsoft.Data.Sqlite;
using ServerMap.Util;
using Vintagestory.API.Common;
using Vintagestory.API.Server;
using Vintagestory.Common;
using Vintagestory.Common.Database;
using Vintagestory.Server;
using Vintagestory.API.MathTools;
using System.Reflection;
using System.Collections.Concurrent;

namespace ServerMap.World;

public sealed class WorldDatabaseReader : IDisposable
{
    private readonly ICoreServerAPI api;
    private readonly ServerMain server;
    private readonly SqliteConnection connection;
    private readonly ChunkDataPool pool;
    private readonly LruCache<long, ServerMapChunk> mapChunks;
    private readonly object mapChunkGate = new();
    private readonly object queryGate = new();
    private Dictionary<(int X, int Z), int[]>? chunkYIndex;
    private readonly object loadedColumnGate = new();
    private readonly Dictionary<(int X, int Z), ServerChunk?[]> loadedColumns = new();
    private readonly Dictionary<(int X, int Z), TaskCompletionSource<ServerChunk?[]>> pendingColumns = new();
    private readonly HashSet<ServerChunk> transientChunks = [];
    private readonly HashSet<long> warnedChunkErrors = [];
    private readonly object blockLayerResolverGate = new();
    private object? blockLayerSystem;
    private object? blockLayerResolver;
    private MethodInfo? getBlockLayerBlock;
    private bool blockLayerResolverLogged;
    private readonly ConcurrentDictionary<(int X, int Y, int Z), int> resolvedBlockLayers = new();
    public WorldMap WorldMap => server.WorldMap;
    public void LogNotification(string message, params object[] args) => api.Logger.Notification(message, args);
    public WorldDatabaseReader(ICoreServerAPI api, int cacheSize)
    {
        this.api = api;
        server = (ServerMain)api.World;
        var thread = Field<ChunkServerThread>(server, "chunkThread")!;
        var db = Field<GameDatabase>(thread, "gameDatabase")!;
        var file = Field<SQLiteDBConnection>(db, "conn")!.GetType().GetField("databaseFileName", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(Field<SQLiteDBConnection>(db, "conn")!) as string ?? throw new InvalidOperationException("Could not resolve world database path");
        connection = new SqliteConnection($"Data Source={file};Mode=ReadOnly;Pooling=false"); connection.Open();
        pool = new ChunkDataPool(32, server);
        mapChunks = new LruCache<long, ServerMapChunk>(cacheSize);
    }

    /// <summary>
    /// Resolves the 1.22.x temporary meta-blocklayer used by schematic
    /// microblocks. Structures replace this id during world generation by
    /// calling BlockSchematicStructure.GetBlockLayerBlock with the active
    /// GenBlockLayers system; retaining the meta id in a saved block entity
    /// is valid for old/partially imported structures, but it is not a
    /// renderable material by itself.
    /// </summary>
    public int? ResolveMetaBlockLayer(int x, int y, int z, int sourceId)
    {
        var source = server.World.GetBlock(sourceId);
        var sourcePath = source?.Code?.Path;
        if (source == null || !string.Equals(sourcePath, "meta-blocklayer", StringComparison.OrdinalIgnoreCase))
            return sourceId;

        if (resolvedBlockLayers.TryGetValue((x, y, z), out var cached))
            return cached > 0 ? cached : null;

        var chunkX = FloorDiv(x, 32);
        var chunkZ = FloorDiv(z, 32);
        var map = GetMapChunk(new ChunkKey(chunkX, 0, chunkZ));
        var climate = map?.MapRegion?.ClimateMap;
        if (map == null || climate == null || climate.InnerSize <= 1 || map.TopRockIdMap is not { Length: > 0 })
            return null;

        try
        {
            var regionChunks = Math.Max(1, api.WorldManager.RegionSize / 32);
            var regionX = chunkX % regionChunks;
            var regionZ = chunkZ % regionChunks;
            if (regionX < 0) regionX += regionChunks;
            if (regionZ < 0) regionZ += regionChunks;
            var scale = climate.InnerSize / (float)regionChunks;
            var climate00 = climate.GetUnpaddedInt((int)(regionX * scale), (int)(regionZ * scale));
            var climate10 = climate.GetUnpaddedInt((int)(regionX * scale + scale), (int)(regionZ * scale));
            var climate01 = climate.GetUnpaddedInt((int)(regionX * scale), (int)(regionZ * scale + scale));
            var climate11 = climate.GetUnpaddedInt((int)(regionX * scale + scale), (int)(regionZ * scale + scale));
            var localX = (x - chunkX * 32) / 32f;
            var localZ = (z - chunkZ * 32) / 32f;
            var climateColor = GameMath.BiLerpRgbColor(localX, localZ, climate00, climate10, climate01, climate11);
            var unscaledRain = (climateColor >> 8) & 0xFF;
            var unscaledTemp = (climateColor >> 16) & 0xFF;
            // BlockSchematicPartial.PlacePartial uses the center sample for
            // the block-entity path in Vintage Story 1.22.x.
            if (map.TopRockIdMap.Length <= 495) return null;
            var rockBlockId = map.TopRockIdMap[495];
            if (rockBlockId <= 0) return null;

            EnsureBlockLayerResolver();
            if (blockLayerResolver == null || getBlockLayerBlock == null)
                return null;

            var blockPos = new BlockPos(x, y, z);
            Block? ResolveAt(int layerY) => getBlockLayerBlock.Invoke(blockLayerResolver,
                // BlockSchematicPartial.PlacePartial passes null as the
                // default block. Passing the temporary source block here
                // would make GetBlockLayerBlock return meta-blocklayer again
                // when no layer rule matches.
                [unscaledRain, unscaledTemp, layerY, rockBlockId, 0, null, server.World.Blocks, blockPos, -1]) as Block;

            // BlockSchematicPartial (the path that imports a
            // BlockEntityMicroBlock) passes the entity position Y.
            var result = ResolveAt(y);
            var resultId = result?.Id ?? 0;
            resolvedBlockLayers[(x, y, z)] = resultId;
            return resultId > 0 ? resultId : null;
        }
        catch (Exception ex)
        {
            if (!blockLayerResolverLogged)
            {
                blockLayerResolverLogged = true;
                api.Logger.Warning("ServerMap could not resolve meta-blocklayer using the 1.22.x GenBlockLayers rule: {0}", ex.Message);
            }
            return null;
        }
    }

    private void EnsureBlockLayerResolver()
    {
        // GenBlockLayers can finish StartServerSide after ServerMap. Do not
        // cache an early null/configuration miss forever: the first render
        // must be able to retry once the vanilla system is initialized.
        if (getBlockLayerBlock != null) return;
        lock (blockLayerResolverGate)
        {
            if (getBlockLayerBlock != null) return;
            try
            {
                blockLayerSystem = api.ModLoader.GetModSystem("Vintagestory.ServerMods.GenBlockLayers");
                blockLayerSystem ??= api.ModLoader.Systems.FirstOrDefault(system =>
                    string.Equals(system.GetType().FullName, "Vintagestory.ServerMods.GenBlockLayers", StringComparison.Ordinal));
                if (blockLayerSystem != null)
                {
                    var schematicType = blockLayerSystem.GetType().Assembly.GetType("Vintagestory.ServerMods.BlockSchematicStructure");
                    var config = blockLayerSystem.GetType().GetField("blockLayerConfig",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(blockLayerSystem);
                    if (schematicType != null && config != null)
                    {
                        blockLayerResolver = Activator.CreateInstance(schematicType);
                        schematicType.GetField("blockLayerConfig",
                            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.SetValue(blockLayerResolver, config);
                        schematicType.GetField("genBlockLayers",
                            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.SetValue(blockLayerResolver, blockLayerSystem);
                        schematicType.GetField("mapheight",
                            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.SetValue(blockLayerResolver, api.World.BlockAccessor.MapSizeY);
                        getBlockLayerBlock = schematicType.GetMethod("GetBlockLayerBlock",
                            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    }
                }
                if (blockLayerResolver == null || getBlockLayerBlock == null)
                {
                    if (!blockLayerResolverLogged)
                    {
                        blockLayerResolverLogged = true;
                        api.Logger.Warning("ServerMap could not initialize the 1.22.x BlockSchematicStructure layer resolver yet; will retry after vanilla GenBlockLayers initialization.");
                    }
                }
            }
            catch (Exception ex)
            {
                if (!blockLayerResolverLogged)
                {
                    blockLayerResolverLogged = true;
                    api.Logger.Warning("ServerMap could not initialize the 1.22.x GenBlockLayers resolver yet; will retry: {0}", ex.Message);
                }
            }
        }
    }

    private static int FloorDiv(int value, int divisor) => value >= 0 ? value / divisor : (value - divisor + 1) / divisor;
    public IEnumerable<ChunkKey> MapColumns()
    {
        List<ChunkKey> result = [];
        lock (queryGate)
        {
            using var cmd = connection.CreateCommand(); cmd.CommandText = "SELECT position FROM mapchunk";
            using var reader = cmd.ExecuteReader(); while (reader.Read()) result.Add(ChunkKey.From(reader.GetInt64(0)));
        }
        return result;
    }

    /// <summary>
    /// Returns the saved vertical chunk slices for a column.  The chunk table
    /// is sparse (especially in worlds with partially generated terrain), so
    /// scanning every Y between 0 and MapSizeY causes thousands of pointless
    /// SQLite reads and leaves the mesh queue looking permanently blank.
    /// </summary>
    public IReadOnlyList<int> ChunkYs(int x, int z)
    {
        lock (queryGate)
        {
            if (chunkYIndex == null)
            {
                var index = new Dictionary<(int X, int Z), List<int>>();
                using var cmd = connection.CreateCommand();
                cmd.CommandText = "SELECT position FROM chunk";
                using var rows = cmd.ExecuteReader();
                while (rows.Read())
                {
                    ChunkKey key;
                    try { key = ChunkKey.From(rows.GetInt64(0)); }
                    catch { continue; }
                    var list = index.GetValueOrDefault((key.X, key.Z));
                    if (list == null) index[(key.X, key.Z)] = list = [];
                    if (!list.Contains(key.Y)) list.Add(key.Y);
                }
                chunkYIndex = index.ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value.OrderBy(value => value).ToArray());
            }

            return chunkYIndex.TryGetValue((x, z), out var values) ? values : Array.Empty<int>();
        }
    }
    public ServerMapChunk? GetMapChunk(ChunkKey key)
    {
        lock (mapChunkGate)
        {
            if (mapChunks.TryGet(key.ToIndex(), out var value)) return value;
            var bytes = Read(key.ToIndex(), "mapchunk"); if (bytes == null) return null;
            value = ServerMapChunk.FromBytes(bytes); mapChunks.Set(key.ToIndex(), value); return value;
        }
    }
    public int? SurfaceHeightAt(double worldX, double worldZ)
    {
        if (!double.IsFinite(worldX) || !double.IsFinite(worldZ)) return null;
        var blockX = (int)Math.Floor(worldX); var blockZ = (int)Math.Floor(worldZ);
        var chunkX = FloorDiv(blockX, 32); var chunkZ = FloorDiv(blockZ, 32);
        var localX = blockX - chunkX * 32; var localZ = blockZ - chunkZ * 32;
        var map = GetMapChunk(new ChunkKey(chunkX, 0, chunkZ));
        if (map == null) return null;
        var index = localZ * 32 + localX;
        if (map.RainHeightMap is { Length: > 0 } rain && index < rain.Length) return rain[index];
        if (map.WorldGenTerrainHeightMap is { Length: > 0 } terrain && index < terrain.Length) return terrain[index];
        return null;
    }
    public ServerChunk? LoadChunk(ChunkKey key)
    {
        var bytes = Read(key.ToIndex(), "chunk"); if (bytes == null) return null;
        return DeserializeChunk(bytes, key);
    }

    /// <summary>Reads a saved chunk, or asynchronously asks the engine to generate a complete column.</summary>
    public ServerChunk? LoadChunkOrGenerate(ChunkKey key)
    {
        var bytes = Read(key.ToIndex(), "chunk");
        if (bytes != null)
        {
            return DeserializeChunk(bytes, key);
        }

        TaskCompletionSource<ServerChunk?[]>? generation = null;
        var startGeneration = false;
        lock (loadedColumnGate)
        {
            if (!loadedColumns.TryGetValue((key.X, key.Z), out var column))
            {
                if (!pendingColumns.TryGetValue((key.X, key.Z), out generation))
                {
                    generation = new TaskCompletionSource<ServerChunk?[]>(TaskCreationOptions.RunContinuationsAsynchronously);
                    pendingColumns[(key.X, key.Z)] = generation;
                    startGeneration = true;
                }
            }
            else
            {
                return key.Y >= 0 && key.Y < column.Length ? column[key.Y] : null;
            }
        }

        if (startGeneration) BeginPeekColumn(key, generation!);
        ServerChunk?[] generated;
        try
        {
            // Generation runs on the server's chunk thread. The render worker
            // waits here, keeping the server thread free to execute the callback.
            generated = generation!.Task.Wait(TimeSpan.FromSeconds(45))
                ? generation.Task.Result
                : [];
        }
        catch (Exception ex)
        {
            generation!.TrySetResult([]);
            api.Logger.Warning("ServerMap chunk peek failed at {0},{1}: {2}", key.X, key.Z, ex.Message);
            generated = [];
        }

        lock (loadedColumnGate)
        {
            pendingColumns.Remove((key.X, key.Z));
            if (!loadedColumns.TryGetValue((key.X, key.Z), out var column))
            {
                loadedColumns[(key.X, key.Z)] = column = generated;
                foreach (var chunk in generated)
                    if (chunk != null) transientChunks.Add(chunk);
            }
            return key.Y >= 0 && key.Y < column.Length ? column[key.Y] : null;
        }
    }

    private void BeginPeekColumn(ChunkKey key, TaskCompletionSource<ServerChunk?[]> completion)
    {
        try
        {
            server.PeekChunkColumn(key.X, key.Z, new ChunkPeekOptions
            {
                UntilPass = EnumWorldGenPass.Done,
                OnGenerated = columns =>
                {
                    try
                    {
                        if (columns == null || columns.Count == 0)
                        {
                            completion.TrySetResult([]);
                            return;
                        }
                        var entry = columns.FirstOrDefault(item => item.Key.X == key.X && item.Key.Y == key.Z);
                        if (entry.Value == null && columns.Count == 1) entry = columns.First();
                        var result = new ServerChunk?[entry.Value?.Length ?? 0];
                        if (entry.Value != null)
                            for (var index = 0; index < entry.Value.Length; index++)
                            {
                                if (entry.Value[index] is not ServerChunk chunk) continue;
                                try
                                {
                                    chunk.Unpack_ReadOnly();
                                    result[index] = chunk;
                                }
                                catch (Exception ex)
                                {
                                    chunk.Dispose();
                                    api.Logger.Warning("ServerMap generated chunk {0},{1},{2} could not be unpacked: {3}", key.X, index, key.Z, ex.Message);
                                }
                            }
                        completion.TrySetResult(result);
                    }
                    catch (Exception ex)
                    {
                        completion.TrySetException(ex);
                    }
                }
            });
        }
        catch (Exception ex)
        {
            completion.TrySetException(ex);
        }
    }

    public void ReleaseChunk(ServerChunk chunk)
    {
        lock (loadedColumnGate)
        {
            if (transientChunks.Contains(chunk)) return;
        }
        chunk.Dispose();
    }

    public void ClearTransientColumns()
    {
        lock (loadedColumnGate)
        {
            foreach (var chunk in transientChunks) chunk.Dispose();
            transientChunks.Clear();
            loadedColumns.Clear();
        }
    }
    private byte[]? Read(long index, string table)
    {
        lock (queryGate)
        {
            using var cmd = connection.CreateCommand(); cmd.CommandText = $"SELECT data FROM {table} WHERE position=@position";
            cmd.Parameters.AddWithValue("@position", index); return cmd.ExecuteScalar() as byte[];
        }
    }
    private ServerChunk? DeserializeChunk(byte[] bytes, ChunkKey key)
    {
        try
        {
            var value = ServerChunk.FromBytes(bytes, pool, server);
            value.Unpack_ReadOnly();
            return value;
        }
        catch (Exception ex)
        {
            lock (queryGate)
            {
                if (warnedChunkErrors.Add(key.ToIndex()))
                    api.Logger.Warning("ServerMap skipped unreadable chunk {0},{1},{2}: {3}", key.X, key.Y, key.Z, ex.Message);
            }
            return null;
        }
    }
    public void Clear()
    {
        mapChunks.Clear();
        lock (queryGate) chunkYIndex = null;
    }

    public void InvalidateChunkIndex()
    {
        lock (queryGate) chunkYIndex = null;
    }
    public void Dispose()
    {
        lock (loadedColumnGate)
        {
            foreach (var chunk in transientChunks) chunk.Dispose();
            loadedColumns.Clear();
            transientChunks.Clear();
            pendingColumns.Clear();
        }
        lock (queryGate) chunkYIndex = null;
        pool.SlowDispose(); connection.Dispose();
    }
    private static T? Field<T>(object instance, string name) where T : class => instance.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)?.GetValue(instance) as T;
}
