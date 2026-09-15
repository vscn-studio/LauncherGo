using System.Text.Json;
using ServerMap.Util;

namespace ServerMap.World;

/// <summary>Completed server-side probes. Density is horizontal; node observations cover a search volume.</summary>
public sealed class OreHeatmapStore
{
    public sealed record OreValue(string Code, double TotalFactor, double PartsPerThousand)
    {
        // Match the vanilla prospecting report: traces, then six density levels.
        public int Density => TotalFactor <= .025 ? 1 : 2 + (int)Math.Clamp(TotalFactor * 7.5, 0, 5);
    }
    public sealed record NodeValue(string Code, int Blocks)
    {
        public int AmountLevel => Blocks <= 0 ? 0 : Blocks < 10 ? 1 : Blocks < 20 ? 2 : Blocks < 40 ? 3 : Blocks < 80 ? 4 : Blocks < 160 ? 5 : 6;
    }
    public sealed record Sample(int ChunkX, int ChunkZ, DateTimeOffset SampledAt, OreValue[] Ores,
        string Mode = "density", int? SampleY = null, int Radius = 0, double? SampleX = null, double? SampleZ = null, NodeValue[]? Nodes = null);
    private sealed record StoredData(int Version, int ChunkSize, Sample[] Samples, int BandSize = 16);
    public sealed record Cell(int ChunkX, int ChunkZ, double MinX, double MinZ, double MaxX, double MaxZ,
        int Density, DateTimeOffset SampledAt, OreValue[] Ores, string Mode, int? SampleY, int Radius,
        double? SampleX, double? SampleZ, NodeValue[] Nodes);
    public sealed record Result(Cell[] Cells, string[] OreCodes, long Version, bool Truncated);
    public const int MaxQueryCells = 10000;
    public const int BandSize = 16;
    public const int MaxNodeRadius = 64;
    private readonly object gate = new();
    private readonly object saveGate = new();
    private readonly string path;
    private readonly int chunkSize;
    private readonly Action<string> log;
    private readonly Dictionary<(int X, int Z, string Mode, int Band), Sample> samples = new();
    private long version = 1;
    private bool dirty;

    public OreHeatmapStore(string path, int chunkSize, Action<string> log)
    {
        if (chunkSize <= 0) throw new ArgumentOutOfRangeException(nameof(chunkSize));
        this.path = path; this.chunkSize = chunkSize; this.log = log;
        if (!File.Exists(path)) return;
        // Fail explicitly on corrupt data; do not silently overwrite it with an empty store.
        var saved = JsonSerializer.Deserialize<StoredData>(File.ReadAllText(path));
        if (saved == null || saved.Version is not (1 or 2) || saved.ChunkSize != chunkSize || saved.BandSize != BandSize || saved.Samples == null)
            throw new InvalidDataException("Unsupported mineral heatmap file: " + path);
        foreach (var sample in saved.Samples)
        {
            if (sample == null || sample.Ores == null || sample.Ores.Any(value => !ValidOre(value))
                || sample.Mode is not ("density" or "node")
                || (double)sample.ChunkX * chunkSize < int.MinValue || (double)sample.ChunkX * chunkSize > int.MaxValue
                || (double)sample.ChunkZ * chunkSize < int.MinValue || (double)sample.ChunkZ * chunkSize > int.MaxValue
                || sample.SampleY is < 0 or > 65535
                || sample.SampleX is double sx && (!Coordinate(sx) || Math.Floor(sx / chunkSize) != sample.ChunkX)
                || sample.SampleZ is double sz && (!Coordinate(sz) || Math.Floor(sz / chunkSize) != sample.ChunkZ)
                || sample.Mode == "node" && (sample.SampleY == null || sample.SampleX == null || sample.SampleZ == null
                    || sample.Radius is < 1 or > MaxNodeRadius || sample.Nodes == null || sample.Nodes.Any(v => !ValidNode(v, sample.Radius))))
                throw new InvalidDataException("Invalid mineral heatmap sample: " + path);
            samples[(sample.ChunkX, sample.ChunkZ, sample.Mode, sample.Mode == "node" ? DepthBand(sample.SampleY!.Value) : 0)] = sample;
        }
        dirty = saved.Version == 1;
    }

    private static bool ValidOre(OreValue? value) => value != null && !string.IsNullOrWhiteSpace(value.Code)
        && value.Code.Length <= 256 && double.IsFinite(value.TotalFactor) && value.TotalFactor >= 0
        && double.IsFinite(value.PartsPerThousand) && value.PartsPerThousand >= 0;

    private static bool Coordinate(double value) => double.IsFinite(value) && value >= int.MinValue && value <= int.MaxValue;
    private static bool ValidNode(NodeValue? value, int radius) => value != null && !string.IsNullOrWhiteSpace(value.Code)
        && value.Code.Length <= 256 && value.Blocks >= 0 && value.Blocks <= Math.Pow(radius * 2 + 1, 3);

    public bool Record(double x, double z, IEnumerable<OreValue> ores, DateTimeOffset sampledAt, int? surfaceY = null)
    {
        if (!Coordinate(x) || !Coordinate(z) || surfaceY is < 0 or > 65535) return false;
        var values = ores.ToArray();
        if (values.Any(value => !ValidOre(value))) return false;
        // Keep only values visible in vanilla's report. Missing ore is not proof of no underground ore.
        values = values.Where(value => value.TotalFactor > .002).OrderByDescending(value => value.TotalFactor).ToArray();
        var chunkX = (int)Math.Floor(x / chunkSize); var chunkZ = (int)Math.Floor(z / chunkSize);
        lock (gate)
        {
            samples[(chunkX, chunkZ, "density", 0)] = new Sample(chunkX, chunkZ, sampledAt, values, SampleY: surfaceY, SampleX: x, SampleZ: z);
            version++; dirty = true;
        }
        return true;
    }

    public bool RecordNode(double x, int y, double z, int radius, IEnumerable<NodeValue> ores, DateTimeOffset sampledAt)
    {
        if (!Coordinate(x) || !Coordinate(z) || y is < 0 or > 65535 || radius is < 1 or > MaxNodeRadius
            || !Coordinate(x - radius) || !Coordinate(x + radius) || !Coordinate(z - radius) || !Coordinate(z + radius)) return false;
        var values = ores.ToArray();
        if (values.Any(value => !ValidNode(value, radius)) || values.Select(v => v.Code).Distinct().Count() != values.Length
            || values.Sum(v => (long)v.Blocks) > Math.Pow(radius * 2 + 1, 3)) return false;
        var cx = (int)Math.Floor(x / chunkSize); var cz = (int)Math.Floor(z / chunkSize);
        var sample = new Sample(cx, cz, sampledAt, [], "node", y, radius, x, z, values.Where(v => v.Blocks > 0).OrderByDescending(v => v.Blocks).ToArray());
        lock (gate) { samples[(cx, cz, "node", DepthBand(y))] = sample; version++; dirty = true; }
        return true;
    }

    public static int DepthBand(int y) => (int)Math.Floor(y / (double)BandSize);

    public Result Query((double MinX, double MinZ, double MaxX, double MaxZ) bounds, string? selectedOre,
        Func<double, double, double, double, bool>? visible = null, string? mode = null, int? minY = null, int? maxY = null)
    {
        lock (gate)
        {
            var cells = new List<Cell>(); var codes = new HashSet<string>(StringComparer.Ordinal);
            var truncated = false;
            foreach (var sample in samples.Values)
            {
                if (!string.IsNullOrEmpty(mode) && !string.Equals(sample.Mode, mode, StringComparison.OrdinalIgnoreCase)) continue;
                if (sample.Mode == "node" && (minY != null && sample.SampleY + sample.Radius < minY || maxY != null && sample.SampleY - sample.Radius > maxY)) continue;
                var minX = sample.ChunkX * (double)chunkSize; var minZ = sample.ChunkZ * (double)chunkSize;
                var maxX = minX + chunkSize; var maxZ = minZ + chunkSize;
                if (maxX < bounds.MinX || minX > bounds.MaxX || maxZ < bounds.MinZ || minZ > bounds.MaxZ) continue;
                if (visible != null && !visible(minX, minZ, maxX, maxZ)) continue;
                // Every chunk and hidden region intersecting the search volume must be authorized.
                if (sample.Mode == "node" && visible != null && !visible(sample.SampleX!.Value - sample.Radius,
                    sample.SampleZ!.Value - sample.Radius, sample.SampleX.Value + sample.Radius + 1, sample.SampleZ.Value + sample.Radius + 1)) continue;
                foreach (var ore in sample.Ores) codes.Add(ore.Code);
                var nodes = sample.Nodes ?? [];
                foreach (var ore in nodes) codes.Add(ore.Code);
                var values = sample.Ores.Where(value => string.IsNullOrEmpty(selectedOre) || value.Code == selectedOre).ToArray();
                nodes = nodes.Where(value => string.IsNullOrEmpty(selectedOre) || value.Code == selectedOre).ToArray();
                var density = values.Length == 0 ? 0 : values.Max(value => value.Density);
                if (cells.Count >= MaxQueryCells) { truncated = true; continue; }
                cells.Add(new Cell(sample.ChunkX, sample.ChunkZ, minX, minZ, maxX, maxZ, density, sample.SampledAt, values,
                    sample.Mode, sample.SampleY, sample.Radius, sample.SampleX, sample.SampleZ, nodes));
            }
            return new Result(cells.ToArray(), codes.OrderBy(code => code, StringComparer.Ordinal).ToArray(), version, truncated);
        }
    }

    public void Save()
    {
        lock (saveGate)
        {
            StoredData snapshot;
            long savedVersion;
            lock (gate)
            {
                if (!dirty) return;
                snapshot = new StoredData(2, chunkSize, samples.Values.ToArray());
                savedVersion = version;
            }
            try
            {
                AtomicFile.Replace(path, temp => File.WriteAllText(temp, JsonSerializer.Serialize(snapshot)));
                lock (gate) { if (version == savedVersion) dirty = false; }
            }
            catch (Exception ex) { log("ServerMap could not save mineral heatmap (will retry): " + ex.Message); }
        }
    }
}
