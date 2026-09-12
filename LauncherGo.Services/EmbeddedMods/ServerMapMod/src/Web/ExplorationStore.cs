using System.Text.Json;
using ServerMap.Render;
using ServerMap.Util;
using Vintagestory.API.Server;

namespace ServerMap.Web;

/// <summary>Permanent, server-authoritative exploration state for the web map.</summary>
public sealed class ExplorationStore : IDisposable
{
    public const int CellSize = 32;
    // Extend the cloud a few pixels into explored cells so a tile boundary or
    // a delayed neighbouring tile can never reveal a sharp unexplored edge.
    private const int FogMarginPixels = 4;
    private sealed class Entry
    {
        public HashSet<long> Cells { get; set; } = new();
        public HashSet<int> Groups { get; set; } = new();
        public long Version { get; set; }
    }
    private readonly string path;
    private readonly object gate = new();
    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private bool disposed;

    public ExplorationStore(string root)
    {
        path = Path.Combine(root, "exploration.json");
        try
        {
            if (File.Exists(path))
            {
                var loaded = JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(path));
                if (loaded != null) foreach (var pair in loaded) entries[pair.Key] = pair.Value ?? new Entry();
            }
        }
        catch { entries.Clear(); }
    }

    public long Version(string uid)
    {
        lock (gate) return entries.GetValueOrDefault(uid)?.Version ?? 0;
    }

    public (int X, int Z)[] VisibleCells(string uid, double minX, double minZ, double maxX, double maxZ, bool share, IEnumerable<IServerPlayer> online, Func<string, bool>? shareWith = null)
    {
        var fromX = FloorDiv((int)Math.Clamp(Math.Floor(Math.Min(minX, maxX)), int.MinValue, int.MaxValue), CellSize);
        var toX = FloorDiv((int)Math.Clamp(Math.Floor(Math.Max(minX, maxX)), int.MinValue, int.MaxValue), CellSize);
        var fromZ = FloorDiv((int)Math.Clamp(Math.Floor(Math.Min(minZ, maxZ)), int.MinValue, int.MaxValue), CellSize);
        var toZ = FloorDiv((int)Math.Clamp(Math.Floor(Math.Max(minZ, maxZ)), int.MinValue, int.MaxValue), CellSize);
        lock (gate) return VisibleCellSetLocked(uid, share, shareWith)
            .Select(key => (X: (int)(key >> 32), Z: (int)key))
            .Where(cell => cell.X >= fromX && cell.X <= toX && cell.Z >= fromZ && cell.Z <= toZ)
            .OrderBy(cell => cell.X).ThenBy(cell => cell.Z).Take(20000).ToArray();
    }

    public bool ContainsOwnCell(string uid, long cell)
    {
        lock (gate) return entries.GetValueOrDefault(uid)?.Cells.Contains(cell) == true;
    }

    // Group snapshots keep offline sharing working; a tick never creates explored terrain.
    public bool UpdateGroups(IServerPlayer player) => RecordNativeChunks(player, []);

    /// <summary>Accept only native map cells whose complete delivery to this player was verified by the sync layer.</summary>
    public bool RecordNativeChunks(IServerPlayer player, IEnumerable<long> verifiedCells)
    {
        if (string.IsNullOrEmpty(player.PlayerUID)) return false;
        var groups = GroupIds(player).ToHashSet();
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var entry = entries.GetValueOrDefault(player.PlayerUID);
            var existed = entry != null;
            entry ??= new Entry();
            var previousGroups = entry.Groups; var previousVersion = entry.Version;
            var added = new List<long>();
            try
            {
                foreach (var cell in verifiedCells) if (entry.Cells.Add(cell)) added.Add(cell);
                if (added.Count == 0 && entry.Groups.SetEquals(groups)) return false;
                entry.Groups = groups; entry.Version++;
                entries[player.PlayerUID] = entry;
                SaveLocked(); // ACKs and in-memory teleport access must agree with durable state.
            }
            catch
            {
                foreach (var cell in added) entry.Cells.Remove(cell);
                entry.Groups = previousGroups; entry.Version = previousVersion;
                if (!existed) entries.Remove(player.PlayerUID);
                throw;
            }
            return true;
        }
    }

    public bool IsVisible(string uid, double x, double z, bool share, IEnumerable<IServerPlayer> online, Func<string, bool>? shareWith = null)
    {
        var cx = FloorDiv((int)Math.Floor(x), CellSize); var cz = FloorDiv((int)Math.Floor(z), CellSize);
        lock (gate)
        {
            if (Contains(entries.GetValueOrDefault(uid), cx, cz)) return true;
            if (!share) return false;
            var viewer = entries.GetValueOrDefault(uid);
            // Group snapshots are persisted, so an offline ally's permanent
            // exploration remains shared as well. The online list is retained
            // in the signature for callers that already have it available.
            foreach (var pair in entries)
            {
                if (pair.Key == uid) continue;
                // Players in the same Vintage Story group share exploration;
                // a map-ID alliance also works without any group or personal
                // exploration record, including when the ally is offline.
                var sameGroup = viewer?.Groups.Overlaps(pair.Value.Groups) == true;
                var allied = shareWith?.Invoke(pair.Key) == true;
                if (!sameGroup && !allied) continue;
                if (Contains(pair.Value, cx, cz)) return true;
            }
            return false;
        }
    }

    /// <summary>
    /// A sparse, merged snapshot of explored cells; never scans the world-sized
    /// bounding box and never truncates distant exploration to a cell quota.
    /// </summary>
    public AreaMarkerStore.Rect[] VisibleRects(string uid, bool share, Func<string, bool>? shareWith = null)
    {
        HashSet<long> cells;
        lock (gate) cells = VisibleCellSetLocked(uid, share, shareWith);
        return AreaMarkerVisibility.Compact(cells.Select(key =>
        {
            var x = (int)(key >> 32) * (long)CellSize; var z = (int)key * (long)CellSize;
            return new AreaMarkerStore.Rect((int)Math.Clamp(x, int.MinValue, int.MaxValue), (int)Math.Clamp(z, int.MinValue, int.MaxValue),
                (int)Math.Clamp(x + CellSize, int.MinValue, int.MaxValue), (int)Math.Clamp(z + CellSize, int.MinValue, int.MaxValue));
        }).Where(r => r.MinX < r.MaxX && r.MinZ < r.MaxZ));
    }

    private HashSet<long> VisibleCellSetLocked(string uid, bool share, Func<string, bool>? shareWith)
    {
        var result = new HashSet<long>();
        var viewer = entries.GetValueOrDefault(uid);
        foreach (var pair in entries)
        {
            if (pair.Key != uid)
            {
                if (!share) continue;
                var sameGroup = viewer?.Groups.Overlaps(pair.Value.Groups) == true;
                var allied = shareWith?.Invoke(pair.Key) == true;
                if (!sameGroup && !allied) continue;
            }
            result.UnionWith(pair.Value.Cells);
        }
        return result;
    }

    public byte[] MaskTile(byte[] png, int zoom, int x, int z, string? uid, bool share, IEnumerable<IServerPlayer> online, Func<string, bool>? shareWith = null)
    {
        if (uid == null) return TransparentTile();
        var pixels = PngEncoder.Decode(png);
        var resolution = 1 << Math.Clamp(zoom, 0, 30);
        var originX = (long)x * 512 * resolution; var originZ = (long)z * 512 * resolution;
        var visible = new Dictionary<(int X, int Z), bool>();
        for (var row = 0; row < 512; row++) for (var col = 0; col < 512; col++)
        {
            var cell = (FloorDiv((int)Math.Clamp(originX + (long)col * resolution, int.MinValue, int.MaxValue), CellSize),
                FloorDiv((int)Math.Clamp(originZ + (long)row * resolution, int.MinValue, int.MaxValue), CellSize));
            if (!visible.TryGetValue(cell, out var allowed))
                visible[cell] = allowed = IsVisible(uid, cell.Item1 * CellSize, cell.Item2 * CellSize, share, online, shareWith);
            if (!allowed) pixels.AsSpan((row * 512 + col) * 4, 4).Clear();
        }
        return PngEncoder.Encode(512, 512, pixels);
    }

    public byte[] FogTile(int zoom, int x, int z, string? uid, bool share, bool bypass, IEnumerable<IServerPlayer> online, Func<string, bool>? shareWith = null)
    {
        var pixels = new byte[512 * 512 * 4];
        if (bypass) return PngEncoder.Encode(512, 512, pixels);
        var resolution = 1 << Math.Clamp(zoom, 0, 30);
        var originX = (long)x * 512 * resolution; var originZ = (long)z * 512 * resolution;
        var visible = new Dictionary<(int X, int Z), bool>();
        var allowedPixels = new bool[512 * 512];
        for (var row = 0; row < 512; row++) for (var col = 0; col < 512; col++)
        {
            var cell = (FloorDiv((int)Math.Clamp(originX + (long)col * resolution, int.MinValue, int.MaxValue), CellSize),
                FloorDiv((int)Math.Clamp(originZ + (long)row * resolution, int.MinValue, int.MaxValue), CellSize));
            if (!visible.TryGetValue(cell, out var cellAllowed))
                visible[cell] = cellAllowed = uid != null && IsVisible(uid, cell.Item1 * CellSize, cell.Item2 * CellSize, share, online, shareWith);
            allowedPixels[row * 512 + col] = cellAllowed;
        }
        var blockedPrefix = new int[513 * 513];
        for (var row = 0; row < 512; row++)
            for (var col = 0; col < 512; col++)
                blockedPrefix[(row + 1) * 513 + col + 1] = (allowedPixels[row * 512 + col] ? 0 : 1)
                    + blockedPrefix[row * 513 + col + 1] + blockedPrefix[(row + 1) * 513 + col] - blockedPrefix[row * 513 + col];
        for (var row = 0; row < 512; row++) for (var col = 0; col < 512; col++)
        {
            var fog = !allowedPixels[row * 512 + col];
            if (!fog)
            {
                var minRow = Math.Max(0, row - FogMarginPixels); var maxRow = Math.Min(511, row + FogMarginPixels);
                var minCol = Math.Max(0, col - FogMarginPixels); var maxCol = Math.Min(511, col + FogMarginPixels);
                fog = blockedPrefix[(maxRow + 1) * 513 + maxCol + 1] - blockedPrefix[minRow * 513 + maxCol + 1]
                    - blockedPrefix[(maxRow + 1) * 513 + minCol] + blockedPrefix[minRow * 513 + minCol] > 0;
                // Also sample just beyond this tile. This gives the cloud a
                // real overlap at tile edges while the neighbouring tile is
                // still loading, so no terrain flashes through the seam.
                if (!fog && (col < FogMarginPixels || col >= 512 - FogMarginPixels || row < FogMarginPixels || row >= 512 - FogMarginPixels))
                {
                    for (var scanRow = row - FogMarginPixels; scanRow <= row + FogMarginPixels && !fog; scanRow++)
                        for (var scanCol = col - FogMarginPixels; scanCol <= col + FogMarginPixels; scanCol++)
                        {
                            if (scanRow >= 0 && scanRow < 512 && scanCol >= 0 && scanCol < 512) continue;
                            var outsideCell = (FloorDiv((int)Math.Clamp(originX + (long)scanCol * resolution, int.MinValue, int.MaxValue), CellSize),
                                FloorDiv((int)Math.Clamp(originZ + (long)scanRow * resolution, int.MinValue, int.MaxValue), CellSize));
                            if (uid == null || !IsVisible(uid, outsideCell.Item1 * CellSize, outsideCell.Item2 * CellSize, share, online, shareWith)) { fog = true; break; }
                        }
                }
            }
            var offset = (row * 512 + col) * 4;
            if (!fog) continue;
            var noise = Noise(originX + (long)col * resolution, originZ + (long)row * resolution);
            // Opaque near-white cloud: the terrain below must be completely
            // concealed. A tiny luminance variation keeps the fog organic
            // without allowing the map to show through.
            var shade = (byte)(248 + noise % 8);
            pixels[offset] = shade; pixels[offset + 1] = shade; pixels[offset + 2] = shade; pixels[offset + 3] = 255;
        }
        return PngEncoder.Encode(512, 512, pixels);
    }

    private static byte[] TransparentTile() => PngEncoder.Encode(512, 512, new byte[512 * 512 * 4]);
    private static int Noise(long x, long z)
    {
        unchecked { var value = (ulong)x * 0x9E3779B97F4A7C15UL ^ (ulong)z * 0xC2B2AE3D27D4EB4FUL; value ^= value >> 30; value *= 0xBF58476D1CE4E5B9UL; value ^= value >> 27; return (int)(value % 28); }
    }
    private static bool Contains(Entry? entry, int x, int z) => entry?.Cells.Contains(Key(x, z)) == true;
    private static long Key(int x, int z) => ((long)(uint)x << 32) | (uint)z;
    private static int FloorDiv(int value, int divisor) => value >= 0 ? value / divisor : (int)(((long)value - divisor + 1) / divisor);
    private static IEnumerable<int> GroupIds(IServerPlayer player) => player.ServerData?.PlayerGroupMemberships?.Keys ?? Enumerable.Empty<int>();
    private void SaveLocked()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        AtomicFile.Replace(path, temp => File.WriteAllText(temp, JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = false })));
    }
    public void Dispose() { lock (gate) disposed = true; }
}
