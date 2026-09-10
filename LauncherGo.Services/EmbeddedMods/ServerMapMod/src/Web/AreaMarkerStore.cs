using System.Text.Json;
using System.Text.RegularExpressions;
using ServerMap.Util;

namespace ServerMap.Web;

/// <summary>Public cartographic names, independent of claims and privacy/fog rules.
/// Geometry is a union of half-open, integer rectangles (one base-map pixel per block),
/// not an allocation proportional to world area. All writes preserve existing neighbours.</summary>
public sealed class AreaMarkerStore
{
    public const int MaxMarkers = 256, MaxRects = 1024, MaxTotalRects = 8192;
    public sealed record Rect(int MinX, int MinZ, int MaxX, int MaxZ);
    public sealed record Appearance(double BorderOpacity = .25, double FillOpacity = .08, double TextOpacity = .58);
    public sealed record Marker(string Id, string Name, string Color, int MinZoom, Rect[] Rects)
    {
        public Appearance Style { get; init; } = new();
    }
    public sealed record Snapshot(long Revision, Marker[] Markers);
    private readonly object gate = new();
    private readonly string path;
    private Snapshot state;
    public AreaMarkerStore(string path)
    {
        this.path = path;
        state = File.Exists(path) ? JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(path)) ?? throw new InvalidDataException("Invalid area markers") : new(0, []);
        if (state.Revision < 0 || state.Markers == null || state.Markers.Length > MaxMarkers || state.Markers.Any(m => m == null || m.Rects == null) || state.Markers.Sum(m => m.Rects.Length) > MaxTotalRects)
            throw new InvalidDataException("Invalid area markers");
        foreach (var marker in state.Markers) { Validate(marker.Name, marker.Color, marker.MinZoom, marker.Rects); ValidateStyle(marker.Style); }
    }
    public Snapshot Read() { lock (gate) return new(state.Revision, state.Markers.Select(m => m with { Rects = m.Rects.ToArray() }).ToArray()); }
    public static int MaxZoom(int minZoom) => minZoom switch { 4 => 6, 7 => 9, 10 => 11, 12 => 13, _ => throw new ArgumentException("Invalid area zoom band") };
    public Marker Save(long revision, string? id, string name, string color, int minZoom, Rect[] rects, Appearance? style = null)
    {
        Validate(name, color, minZoom, rects);
        lock (gate)
        {
            if (revision != state.Revision) throw new InvalidOperationException("Area markers changed; reload before saving");
            if (!string.IsNullOrEmpty(id) && !state.Markers.Any(m => m.Id == id)) throw new KeyNotFoundException();
            if (string.IsNullOrEmpty(id) && state.Markers.Length >= MaxMarkers) throw new InvalidOperationException("Area marker limit reached (256)");
            style ??= state.Markers.FirstOrDefault(m => m.Id == id)?.Style ?? new();
            ValidateStyle(style);
            var budget = 2_000_000;
            var normalized = Union(rects, ref budget);
            // Even stale/concurrent clients cannot overwrite neighbours in the same band.
            foreach (var occupied in state.Markers.Where(m => m.Id != id && m.MinZoom == minZoom).SelectMany(m => m.Rects))
                normalized = Cut(normalized, occupied, ref budget);
            if (normalized.Count == 0) throw new ArgumentException("Select an unoccupied area");
            if (state.Markers.Where(m => m.Id != id).Sum(m => m.Rects.Length) + normalized.Count > MaxTotalRects)
                throw new InvalidOperationException("Area geometry limit reached");
            var marker = new Marker(string.IsNullOrEmpty(id) ? Guid.NewGuid().ToString("N") : id, name.Trim(), color.ToLowerInvariant(), minZoom, normalized.ToArray()) { Style = style };
            Commit(new(state.Revision + 1, state.Markers.Where(m => m.Id != marker.Id).Append(marker).ToArray()));
            return marker with { Rects = marker.Rects.ToArray() };
        }
    }
    public Marker Merge(long revision, string[] sourceIds, string name, string color, int minZoom, Appearance? style = null)
    {
        _ = MaxZoom(minZoom);
        if (sourceIds == null || sourceIds.Length is < 2 or > MaxMarkers || sourceIds.Any(string.IsNullOrWhiteSpace) || sourceIds.Distinct(StringComparer.Ordinal).Count() != sourceIds.Length)
            throw new ArgumentException("Select at least two distinct source areas");
        lock (gate)
        {
            if (revision != state.Revision) throw new InvalidOperationException("Area markers changed; reload before merging");
            var sources = sourceIds.Select(id => state.Markers.FirstOrDefault(m => m.Id == id) ?? throw new KeyNotFoundException()).ToArray();
            if (sources.Any(m => minZoom >= m.MinZoom)) throw new ArgumentException("Merged area must be a higher level than every source");
            var budget = 2_000_000;
            var rects = Union(sources.SelectMany(m => m.Rects), ref budget).ToArray();
            // Geometry comes only from the server-owned originals, never a client-supplied bounding box.
            return Save(revision, null, name, color, minZoom, rects, style);
        }
    }
    private static List<Rect> Union(IEnumerable<Rect> rects, ref int budget)
    {
        var normalized = new List<Rect>();
        foreach (var rect in rects)
        {
            var pieces = new List<Rect> { rect };
            foreach (var occupied in normalized) pieces = Cut(pieces, occupied, ref budget);
            normalized.AddRange(pieces); CheckCount(normalized.Count);
        }
        return normalized;
    }
    private static void ValidateStyle(Appearance style)
    {
        if (style == null || new[] { style.BorderOpacity, style.FillOpacity, style.TextOpacity }.Any(v => !double.IsFinite(v) || v < 0 || v > 1))
            throw new ArgumentException("Opacity must be between zero and one");
    }
    public bool Remove(long revision, string id)
    {
        lock (gate)
        {
            if (revision != state.Revision) throw new InvalidOperationException("Area markers changed; reload before deleting");
            if (!state.Markers.Any(m => m.Id == id)) return false;
            Commit(new(state.Revision + 1, state.Markers.Where(m => m.Id != id).ToArray())); return true;
        }
    }
    public static bool Intersects(Rect a, Rect b) => a.MinX < b.MaxX && a.MaxX > b.MinX && a.MinZ < b.MaxZ && a.MaxZ > b.MinZ;
    private static List<Rect> Cut(List<Rect> input, Rect cut, ref int budget)
    {
        var output = new List<Rect>();
        foreach (var r in input)
        {
            if (--budget < 0) throw new ArgumentException("Area selection is too complex");
            if (!Intersects(r, cut)) output.Add(r);
            else
            {
                var x1 = Math.Max(r.MinX, cut.MinX); var x2 = Math.Min(r.MaxX, cut.MaxX);
                var z1 = Math.Max(r.MinZ, cut.MinZ); var z2 = Math.Min(r.MaxZ, cut.MaxZ);
                if (r.MinZ < z1) output.Add(new(r.MinX, r.MinZ, r.MaxX, z1));
                if (z2 < r.MaxZ) output.Add(new(r.MinX, z2, r.MaxX, r.MaxZ));
                if (r.MinX < x1) output.Add(new(r.MinX, z1, x1, z2));
                if (x2 < r.MaxX) output.Add(new(x2, z1, r.MaxX, z2));
            }
            CheckCount(output.Count);
        }
        return output;
    }
    private static void CheckCount(int count) { if (count > MaxRects) throw new ArgumentException("Area selection is too complex (1024 rectangles)"); }
    private static void Validate(string name, string color, int zoom, Rect[] rects)
    {
        _ = MaxZoom(zoom);
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 80 || !Regex.IsMatch(color ?? "", "^#[0-9a-fA-F]{6}$") || rects == null || rects.Length is < 1 or > MaxRects)
            throw new ArgumentException("Invalid area marker");
        foreach (var r in rects)
            if (r == null || r.MinX < -32_000_000 || r.MinZ < -32_000_000 || r.MaxX > 32_000_000 || r.MaxZ > 32_000_000 || r.MinX >= r.MaxX || r.MinZ >= r.MaxZ)
                throw new ArgumentException("Invalid area rectangle");
    }
    private void Commit(Snapshot next) { AtomicFile.Replace(path, temp => File.WriteAllText(temp, JsonSerializer.Serialize(next))); state = next; }
}
