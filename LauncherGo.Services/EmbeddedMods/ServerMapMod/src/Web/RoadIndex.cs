using System.Collections.Concurrent;
using ServerMap.Render;
using ServerMap.World;
using Vintagestory.API.Common;
using Vintagestory.API.Server;
using Vintagestory.Server;

namespace ServerMap.Web;

/// <summary>
/// Lightweight, derived index for roads.  The durable surface cache is the
/// source of truth for normal roads; optional deep scanning only reads sparse
/// chunk columns around a cached surface tile.  Nothing is written to
/// the world database and the index can therefore be discarded at any time.
/// </summary>
public sealed partial class RoadIndex
{
    private readonly record struct RoadPosition(int X, int Y, int Z);
    private sealed record TileRoads(IReadOnlyList<Cell> Cells);

    private readonly ICoreServerAPI api;
    private readonly string root;
    private readonly WorldDatabaseReader reader;
    private readonly MapPalette materials;
    private ConcurrentDictionary<string, TileRoads> tiles = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, double> speedCache = new(StringComparer.OrdinalIgnoreCase);

    public RoadIndex(ICoreServerAPI api, string root, WorldDatabaseReader reader, MapPalette materials)
    {
        this.api = api;
        this.root = root;
        this.reader = reader;
        this.materials = materials;
    }

    public void Invalidate()
    {
        // An old in-flight scan may finish, but must not repopulate the cache
        // used by requests after a saved world change.
        Interlocked.Exchange(ref tiles, new ConcurrentDictionary<string, TileRoads>(StringComparer.Ordinal));
        speedCache.Clear();
    }

    public IReadOnlyList<Segment> Query((double MinX, double MinZ, double MaxX, double MaxZ)? bounds,
        MapManagementSettings.RoadSettings settings, IEnumerable<(int X, int Z)> regions,
        System.Func<double, double, bool>? visible = null)
    {
        var codes = (settings.BlockCodes ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (codes.Count == 0) return [];
        var key = CacheKey(settings);
        var snapshot = Volatile.Read(ref tiles);
        var cells = new Dictionary<(int X, int Z), Cell>();
        // Include a small border so a line crossing a tile/bbox boundary keeps
        // its centre instead of producing a visibly offset end point.
        var query = bounds ?? (double.MinValue / 4, double.MinValue / 4, double.MaxValue / 4, double.MaxValue / 4);
        const double border = 64;
        foreach (var region in regions)
        {
            var minX = region.X * 512d; var minZ = region.Z * 512d;
            var maxX = minX + 512; var maxZ = minZ + 512;
            if (bounds != null && (maxX < query.MinX - border || minX > query.MaxX + border || maxZ < query.MinZ - border || minZ > query.MaxZ + border)) continue;
            var tile = snapshot.GetOrAdd($"{region.X}:{region.Z}:{key}", _ => ScanTile(region.X, region.Z, codes, settings));
            foreach (var cell in tile.Cells)
            {
                if (bounds != null && (cell.X < query.MinX - border || cell.X > query.MaxX + border || cell.Z < query.MinZ - border || cell.Z > query.MaxZ + border)) continue;
                // A deep candidate and a surface candidate can resolve to the
                // same coordinate. Prefer the surface value (the first value
                // inserted by ScanTile) and never emit stacked parallel roads.
                cells.TryAdd((cell.X, cell.Z), cell);
            }
        }
        // Use one stable range for the configured road materials.  A fixed
        // 1.0..2.5 scale made ordinary paths look yellow (or made every
        // segment look identical); the endpoints now follow the actual
        // multipliers of the roads selected in the management window.
        var configuredSpeeds = codes.Select(Speed).Where(value => double.IsFinite(value) && value > 0).ToArray();
        var minSpeed = configuredSpeeds.Length == 0 ? 1d : configuredSpeeds.Min();
        var maxSpeed = configuredSpeeds.Length == 0 ? 1d : configuredSpeeds.Max();
        return BuildSegments(cells.Values, bounds, minSpeed, maxSpeed, visible);
    }

    private TileRoads ScanTile(int tileX, int tileZ, HashSet<string> codes, MapManagementSettings.RoadSettings settings)
    {
        var surface = SurfaceRegion.Load(SurfaceRegion.PathFor(root, tileX, tileZ));
        if (surface?.Width != SurfaceRegion.Size) return new TileRoads([]);
        var found = new Dictionary<(int X, int Z), Cell>();
        var chunks = new Dictionary<ChunkKey, ServerChunk?>();
        try
        {
            for (var pz = 0; pz < SurfaceRegion.Size; pz++) for (var px = 0; px < SurfaceRegion.Size; px++)
            {
                var index = pz * SurfaceRegion.Size + px;
                if (!surface.Valid[index]) continue;
                var x = tileX * 512 + px; var z = tileZ * 512 + pz;
                var code = surface.Codes[index] ?? "";
                if (codes.Contains(code))
                {
                    found[(x, z)] = new Cell(x, z, surface.Heights[index], code, Speed(code));
                    continue;
                }
            }

            // Deep scanning is deliberately a relay from road ends/edges,
            // rather than a downward scan of every terrain column.  This
            // prevents unrelated buried paths from appearing while still
            // allowing a road to enter a cave or climb behind an overhang.
            if (settings.DeepScan && found.Count > 0)
            {
                var states = new HashSet<RoadPosition>();
                var queue = new Queue<RoadPosition>();
                foreach (var cell in found.Values)
                {
                    var position = new RoadPosition(cell.X, cell.Y, cell.Z);
                    states.Add(position);
                    if (IsRelaySeed(cell, found)) queue.Enqueue(position);
                }

                var codeCache = new Dictionary<RoadPosition, string>();
                while (queue.Count > 0)
                {
                    var current = queue.Dequeue();

                    // First look in the same column.  The reference height is
                    // current.Y (the road block itself), never the cached
                    // terrain/surface height.  Check both directions so both
                    // descending and ascending stair entrances work.
                    foreach (var candidate in FindVerticalRoads(current, settings.RelayDepth, codes, chunks, codeCache))
                        AddRelay(candidate, states, queue, found, chunks, codeCache);

                    // Once a relay block is found, follow cardinal neighbours
                    // with the same small vertical tolerance.  A one-block
                    // stair therefore remains connected even when its top is
                    // hidden by a solid block or another map surface.
                    foreach (var (nx, nz) in Neighbours((current.X, current.Z)))
                    {
                        if (nx < tileX * SurfaceRegion.Size || nx >= (tileX + 1) * SurfaceRegion.Size
                            || nz < tileZ * SurfaceRegion.Size || nz >= (tileZ + 1) * SurfaceRegion.Size) continue;
                        var candidate = FindNeighbourRoad(nx, nz, current.Y, settings.RelayDepth, codes, chunks, codeCache);
                        if (candidate is { } position) AddRelay(position, states, queue, found, chunks, codeCache);
                    }
                }
            }
        }
        finally
        {
            foreach (var chunk in chunks.Values)
                if (chunk != null) reader.ReleaseChunk(chunk);
        }
        return new TileRoads(found.Values.ToArray());
    }

    private static bool IsRelaySeed(Cell cell, IReadOnlyDictionary<(int X, int Z), Cell> surfaceRoads)
    {
        var neighbours = 0;
        foreach (var neighbour in Neighbours((cell.X, cell.Z)))
            if (surfaceRoads.ContainsKey(neighbour)) neighbours++;
        // End points and every exposed edge of a wide road are valid relay
        // origins.  Interior cells are intentionally not probed.
        return neighbours <= 1 || neighbours < 4;
    }

    private void AddRelay(RoadPosition position, HashSet<RoadPosition> states, Queue<RoadPosition> queue,
        Dictionary<(int X, int Z), Cell> found, Dictionary<ChunkKey, ServerChunk?> chunks,
        Dictionary<RoadPosition, string> cache)
    {
        if (!states.Add(position)) return;
        var code = BlockCode(position.X, position.Y, position.Z, chunks, cache);
        if (code.Length == 0) return;
        // Keep one rendered cell for stacked blocks.  Surface cells were
        // inserted first, so a deep relay can never replace the visible road
        // at the same X/Z while it can still continue the 3D traversal.
        found.TryAdd((position.X, position.Z), new Cell(position.X, position.Z, position.Y, code, Speed(code)));
        queue.Enqueue(position);
    }

    private IEnumerable<RoadPosition> FindVerticalRoads(RoadPosition origin, int depth, HashSet<string> codes,
        Dictionary<ChunkKey, ServerChunk?> chunks, Dictionary<RoadPosition, string> cache)
    {
        for (var distance = 1; distance <= depth; distance++)
        {
            var below = origin with { Y = origin.Y - distance };
            if (TryRoad(below, codes, chunks, cache, out _)) yield return below;
            var above = origin with { Y = origin.Y + distance };
            if (TryRoad(above, codes, chunks, cache, out _)) yield return above;
        }
    }

    private RoadPosition? FindNeighbourRoad(int x, int z, int baseY, int depth, HashSet<string> codes,
        Dictionary<ChunkKey, ServerChunk?> chunks, Dictionary<RoadPosition, string> cache)
    {
        for (var distance = 0; distance <= depth; distance++)
        {
            var down = new RoadPosition(x, baseY - distance, z);
            if (TryRoad(down, codes, chunks, cache, out _)) return down;
            if (distance == 0) continue;
            var up = new RoadPosition(x, baseY + distance, z);
            if (TryRoad(up, codes, chunks, cache, out _)) return up;
        }
        return null;
    }

    private bool TryRoad(RoadPosition position, HashSet<string> codes, Dictionary<ChunkKey, ServerChunk?> chunks,
        Dictionary<RoadPosition, string> cache, out string code)
    {
        code = BlockCode(position.X, position.Y, position.Z, chunks, cache);
        return codes.Contains(code);
    }

    private string BlockCode(int x, int y, int z, Dictionary<ChunkKey, ServerChunk?> chunks,
        Dictionary<RoadPosition, string> cache)
    {
        if (y < 0) return "";
        var position = new RoadPosition(x, y, z);
        if (cache.TryGetValue(position, out var cached)) return cached;
        var id = ReadBlock(x, y, z, chunks);
        var code = id > 0 ? materials.Get(id).Code : "";
        cache[position] = code;
        return code;
    }

    private int ReadBlock(int x, int y, int z, Dictionary<ChunkKey, ServerChunk?> chunks)
    {
        if (y < 0) return 0;
        var cx = FloorDiv(x, 32); var cz = FloorDiv(z, 32); var cy = FloorDiv(y, 32);
        var key = new ChunkKey(cx, cy, cz);
        if (!chunks.TryGetValue(key, out var chunk))
        {
            chunk = reader.LoadChunk(key);
            chunks[key] = chunk;
        }
        if (chunk == null) return 0;
        var lx = x - cx * 32; var lz = z - cz * 32;
        var index = lx + lz * 32 + (y & 31) * 1024;
        return chunk.Data.GetBlockId(index, 0);
    }

    private double Speed(string code)
    {
        if (speedCache.TryGetValue(code, out var cached)) return cached;
        var speed = 1d;
        try
        {
            var block = api.World.GetBlock(new AssetLocation(code));
            // Block.WalkSpeedMultiplier is populated by Vintage Story from
            // the block definition (for example stonepath-free = 1.30).
            // The JSON attribute is only a compatibility fallback for older
            // mod blocks that do not expose a valid runtime value.
            var value = block?.WalkSpeedMultiplier ?? 1f;
            if (!float.IsFinite(value) || value <= 0)
            {
                var attribute = block?.Attributes?["walkspeedmultiplier"] ?? block?.Attributes?["walkSpeedMultiplier"];
                value = attribute?.AsFloat(1f) ?? 1f;
            }
            speed = double.IsFinite(value) && value > 0 ? Math.Clamp(value, .1, 10) : 1;
        }
        catch { speed = 1; }
        speedCache.TryAdd(code, speed);
        return speed;
    }

    private static string CacheKey(MapManagementSettings.RoadSettings settings) =>
        string.Join(",", (settings.BlockCodes ?? []).OrderBy(code => code, StringComparer.OrdinalIgnoreCase).Select(code => code.ToLowerInvariant()))
        + $"|{settings.DeepScan}|{settings.RelayDepth}";

    private static int FloorDiv(int value, int divisor) => value >= 0 ? value / divisor : (value - divisor + 1) / divisor;
}
