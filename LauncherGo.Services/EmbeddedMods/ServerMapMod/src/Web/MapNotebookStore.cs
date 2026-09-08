using System.Text.Json;
using ServerMap.Util;

namespace ServerMap.Web;

/// <summary>World-scoped, server-owned private routes, immutable shares and fog regions.</summary>
public sealed class MapNotebookStore
{
    public sealed record Route(string Id, string OwnerUid, string Name, string Color, double[][] Points, DateTimeOffset UpdatedAt);
    public sealed record Share(string Id, string SourceId, Route Snapshot);
    public sealed record Region(string Id, string Name, double MinX, double MinZ, double MaxX, double MaxZ, bool HideInGame = false);
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
        foreach (var region in state.Regions) ValidateBounds(region.MinX, region.MinZ, region.MaxX, region.MaxZ);
    }
    public Region[] Regions { get { lock (gate) return state.Regions.ToArray(); } }
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
    private static string Limit(string? value, string fallback) { value = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim(); return value.Length <= 80 ? value : value[..80]; }
    private static string NormalizeColor(string? color) => System.Text.RegularExpressions.Regex.IsMatch(color ?? "", "^#[0-9a-fA-F]{6}$") ? color! : "#ffd000";
}
