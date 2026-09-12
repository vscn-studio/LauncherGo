using System.Text.Json;
using ServerMap.Util;

namespace ServerMap.Web;

/// <summary>World-scoped, server-owned private routes, immutable shares and fog regions.</summary>
public sealed class MapNotebookStore
{
    public sealed record Route(string Id, string OwnerUid, string Name, string Color, double[][] Points, DateTimeOffset UpdatedAt);
    public sealed record Share(string Id, string SourceId, Route Snapshot);
    public sealed record Region(string Id, string Name, double MinX, double MinZ, double MaxX, double MaxZ, bool HideInGame = false)
    {
        // Null denotes an older inclusive rectangle. New shapes are unions of
        // half-open integer rectangles; the bounds are only a navigation envelope.
        public AreaMarkerStore.Rect[]? Rects { get; init; }
        // Teleport quotes compare independent snapshots. Array reference equality
        // would report a privacy change on every read even without an actual edit.
        public bool Equals(Region? other) => other != null && Id == other.Id && Name == other.Name && MinX == other.MinX && MinZ == other.MinZ && MaxX == other.MaxX && MaxZ == other.MaxZ && HideInGame == other.HideInGame
            && (Rects == null ? other.Rects == null : other.Rects != null && Rects.SequenceEqual(other.Rects));
        public override int GetHashCode()
        {
            var hash = new HashCode(); hash.Add(Id); hash.Add(Name); hash.Add(MinX); hash.Add(MinZ); hash.Add(MaxX); hash.Add(MaxZ); hash.Add(HideInGame);
            if (Rects != null) foreach (var rect in Rects) hash.Add(rect);
            return hash.ToHashCode();
        }
    }
    private sealed record State(Route[] Routes, Share[] Shares, Region[] Regions);
    private readonly string path;
    private readonly object gate = new();
    private State state;
    public MapNotebookStore(string path)
    {
        this.path = path;
        // Do not silently discard unreadable fog rules and expose protected terrain.
        state = File.Exists(path) ? JsonSerializer.Deserialize<State>(File.ReadAllText(path)) ?? throw new InvalidDataException("Invalid map notebook") : new([], [], []);
        if (state.Routes is null || state.Shares is null || state.Regions is null) throw new InvalidDataException("Invalid map notebook");
        if (state.Regions.Length > 256 || state.Regions.Sum(r => r.Rects?.Length ?? 1) > AreaMarkerStore.MaxTotalRects) throw new InvalidDataException("Too many hidden regions");
        foreach (var region in state.Regions)
        {
            ValidateBounds(region.MinX, region.MinZ, region.MaxX, region.MaxZ);
            if (region.Rects != null)
            {
                AreaMarkerStore.ValidateRects(region.Rects);
                if (region.MinX != region.Rects.Min(r => r.MinX) || region.MinZ != region.Rects.Min(r => r.MinZ) || region.MaxX != region.Rects.Max(r => r.MaxX) || region.MaxZ != region.Rects.Max(r => r.MaxZ))
                    throw new InvalidDataException("Invalid hidden region bounds");
            }
        }
    }
    public Region[] Regions { get { lock (gate) return state.Regions.Select(Clone).ToArray(); } }
    public static AreaMarkerStore.Rect[] PixelRects(Region region) => region.Rects?.ToArray() ??
        [new((int)Math.Floor(region.MinX), (int)Math.Floor(region.MinZ), (int)Math.Min(32_000_000, Math.Floor(region.MaxX) + 1), (int)Math.Min(32_000_000, Math.Floor(region.MaxZ) + 1))];
    public Route[] ForOwner(string uid) { lock (gate) return state.Routes.Where(r => r.OwnerUid == uid).Select(Clone).ToArray(); }
    public Route Save(string uid, string? id, string name, string color, double[][] points)
    {
        ValidatePoints(points);
        lock (gate)
        {
            var existing = string.IsNullOrEmpty(id) ? null : state.Routes.FirstOrDefault(r => r.Id == id && r.OwnerUid == uid) ?? throw new UnauthorizedAccessException();
            if (existing is null && state.Routes.Count(r => r.OwnerUid == uid) >= 100) throw new InvalidOperationException("Route limit reached (100)");
            var route = new Route(existing?.Id ?? Guid.NewGuid().ToString("N"), uid, Limit(name, "Route"), NormalizeColor(color), points.Select(p => p.ToArray()).ToArray(), DateTimeOffset.UtcNow);
            Commit(state with { Routes = state.Routes.Where(r => r.Id != route.Id).Append(route).ToArray() });
            return Clone(route);
        }
    }
    public bool Remove(string uid, string id)
    {
        lock (gate)
        {
            if (!state.Routes.Any(r => r.Id == id && r.OwnerUid == uid)) return false;
            Commit(state with { Routes = state.Routes.Where(r => r.Id != id).ToArray(), Shares = state.Shares.Where(s => s.SourceId != id).ToArray() });
            return true;
        }
    }
    public string ShareRoute(string uid, string id)
    {
        lock (gate)
        {
            var route = state.Routes.FirstOrDefault(r => r.Id == id && r.OwnerUid == uid) ?? throw new UnauthorizedAccessException();
            var existing = state.Shares.FirstOrDefault(s => s.SourceId == id && s.Snapshot.UpdatedAt == route.UpdatedAt);
            if (existing != null) return existing.Id;
            if (state.Shares.Count(s => s.Snapshot.OwnerUid == uid) >= 200) throw new InvalidOperationException("Share limit reached (200); remove old routes to revoke shares");
            var share = new Share(Guid.NewGuid().ToString("N"), id, Clone(route));
            Commit(state with { Shares = state.Shares.Append(share).ToArray() });
            return share.Id;
        }
    }
    public Route? Shared(string id) { lock (gate) { var route = state.Shares.FirstOrDefault(s => s.Id == id)?.Snapshot; return route is null ? null : Clone(route); } }
    public Region SaveRegion(string? id, string name, double x1, double z1, double x2, double z2, bool hideInGame = false)
    {
        var minX = Math.Min(x1, x2); var minZ = Math.Min(z1, z2); var maxX = Math.Max(x1, x2); var maxZ = Math.Max(z1, z2);
        ValidateBounds(minX, minZ, maxX, maxZ);
        lock (gate)
        {
            if (!string.IsNullOrEmpty(id) && !state.Regions.Any(r => r.Id == id)) throw new KeyNotFoundException();
            if (string.IsNullOrEmpty(id) && state.Regions.Length >= 256) throw new InvalidOperationException("Hidden region limit reached (256)");
            var region = new Region(string.IsNullOrEmpty(id) ? Guid.NewGuid().ToString("N") : id, Limit(name, "Hidden region"), minX, minZ, maxX, maxZ, hideInGame);
            if (state.Regions.Where(r => r.Id != id).Sum(r => r.Rects?.Length ?? 1) + 1 > AreaMarkerStore.MaxTotalRects) throw new InvalidOperationException("Hidden region geometry limit reached");
            Commit(state with { Regions = state.Regions.Where(r => r.Id != region.Id).Append(region).ToArray() });
            return region;
        }
    }
    public bool RemoveRegion(string id)
    {
        lock (gate)
        {
            if (!state.Regions.Any(r => r.Id == id)) return false;
            Commit(state with { Regions = state.Regions.Where(r => r.Id != id).ToArray() }); return true;
        }
    }
    public Region SaveRegion(string? id, string name, AreaMarkerStore.Rect[] rects, bool hideInGame = false)
    {
        lock (gate)
        {
            if (!string.IsNullOrEmpty(id) && !state.Regions.Any(r => r.Id == id)) throw new KeyNotFoundException();
            if (string.IsNullOrEmpty(id) && state.Regions.Length >= 256) throw new InvalidOperationException("Hidden region limit reached (256)");
            var others = state.Regions.Where(r => r.Id != id).ToArray();
            var shape = AreaMarkerStore.NormalizeRects(rects, others.SelectMany(PixelRects));
            if (others.Sum(r => r.Rects?.Length ?? 1) + shape.Length > AreaMarkerStore.MaxTotalRects) throw new InvalidOperationException("Hidden region geometry limit reached");
            var region = new Region(string.IsNullOrEmpty(id) ? Guid.NewGuid().ToString("N") : id, Limit(name, "Hidden region"), shape.Min(r => r.MinX), shape.Min(r => r.MinZ), shape.Max(r => r.MaxX), shape.Max(r => r.MaxZ), hideInGame) { Rects = shape };
            Commit(state with { Regions = others.Append(region).ToArray() });
            return Clone(region);
        }
    }
    public static bool ValidCoordinate(double value) => double.IsFinite(value) && Math.Abs(value) <= 32_000_000;
    public static void ValidatePoints(double[][] points)
    {
        if (points is null || points.Length is < 2 or > 512 || points.Any(p => p is null || p.Length != 2 || !ValidCoordinate(p[0]) || !ValidCoordinate(p[1])))
            throw new ArgumentException("A route requires 2–512 finite X/Z points within world coordinate limits");
    }
    private static void ValidateBounds(double minX, double minZ, double maxX, double maxZ)
    {
        if (!ValidCoordinate(minX) || !ValidCoordinate(minZ) || !ValidCoordinate(maxX) || !ValidCoordinate(maxZ) || maxX - minX < 1 || maxZ - minZ < 1)
            throw new ArgumentException("A hidden region must be at least one block wide and deep");
    }
    private void Commit(State next) { AtomicFile.Replace(path, temp => File.WriteAllText(temp, JsonSerializer.Serialize(next))); state = next; }
    private static Route Clone(Route route) => route with { Points = route.Points.Select(p => p.ToArray()).ToArray() };
    private static Region Clone(Region region) => region with { Rects = region.Rects?.ToArray() };
    private static string Limit(string? value, string fallback) { value = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim(); return value.Length <= 80 ? value : value[..80]; }
    private static string NormalizeColor(string? color) => System.Text.RegularExpressions.Regex.IsMatch(color ?? "", "^#[0-9a-fA-F]{6}$") ? color! : "#ffd000";
}
