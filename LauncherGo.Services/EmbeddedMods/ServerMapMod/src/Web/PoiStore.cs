using System.Collections.Concurrent;
using System.Text.Json;
using ServerMap.Util;

namespace ServerMap.Web;

public sealed class PoiStore
{
    public enum SaveResult { Saved, QuotaExceeded, Forbidden }
    public sealed record Poi(string Id, string Type, string Name, string Text, string Color, double Rotation, double X, double Z, double? X2, double? Z2, string OwnerUid, DateTimeOffset UpdatedAt)
    {
        public string? ImageKey { get; init; }
        public string? ImageAddedBy { get; init; }
        public string? ImageAddedByUid { get; init; }
        public int MinZoom { get; init; } = 13;
        public int MaxZoom { get; init; } = 15;
    }
    private readonly string path; private readonly object gate = new(); private readonly ConcurrentDictionary<string, Poi> points = new();
    public PoiStore(string path)
    {
        this.path = path;
        try
        {
            if (!File.Exists(path)) return;
            var changed = false;
            foreach (var point in JsonSerializer.Deserialize<Poi[]>(File.ReadAllText(path)) ?? [])
            {
                var rotation = NormalizeRotation(point.Rotation);
                changed |= rotation != point.Rotation;
                points[point.Id] = point with { Rotation = rotation };
            }
            if (changed) PersistLocked();
        }
        catch { }
    }
    public static bool ValidRotation(double rotation) => double.IsFinite(rotation) && rotation >= -60 && rotation <= 60;
    public static double NormalizeRotation(double rotation) => ValidRotation(rotation) ? rotation : 0;
    public static bool ValidZoomRange(int minZoom, int maxZoom) => minZoom >= 4 && maxZoom <= 15 && maxZoom >= minZoom;
    public IReadOnlyCollection<Poi> All => points.Values.ToArray();
    public SaveResult TrySave(Poi input, string ownerUid, int maxPerOwner, bool canManageAll, out Poi? saved, bool replaceImage = false, bool replaceZoom = false)
    {
        lock (gate)
        {
            Poi? existing = null;
            var updating = !string.IsNullOrWhiteSpace(input.Id) && points.TryGetValue(input.Id, out existing);
            if (updating && existing!.OwnerUid != ownerUid && !canManageAll) { saved = null; return SaveResult.Forbidden; }
            if (replaceZoom && !canManageAll) { saved = null; return SaveResult.Forbidden; }
            if (replaceZoom && !ValidZoomRange(input.MinZoom, input.MaxZoom)) throw new ArgumentException("Invalid zoom range");
            if (!updating && points.Values.Count(point => point.OwnerUid == ownerUid) >= Math.Max(0, maxPerOwner)) { saved = null; return SaveResult.QuotaExceeded; }
            var type = input.Type is "rectangle" or "text" ? input.Type : "point";
            var inputColor = input.Color ?? "";
            var color = System.Text.RegularExpressions.Regex.IsMatch(inputColor, "^#[0-9a-fA-F]{6}$") ? inputColor : "#e66c75";
            var rotation = NormalizeRotation(input.Rotation);
            var id = updating ? existing!.Id : Guid.NewGuid().ToString("N");
            var persistedOwner = updating ? existing!.OwnerUid : ownerUid;
            saved = new Poi(id, type, Limit(input.Name, 80, "POI"), Limit(input.Text, 500, ""), color, rotation, input.X, input.Z, input.X2, input.Z2, persistedOwner, DateTimeOffset.UtcNow) { ImageKey = replaceImage ? input.ImageKey : existing?.ImageKey, MinZoom = replaceZoom ? input.MinZoom : existing?.MinZoom ?? 13, MaxZoom = replaceZoom ? input.MaxZoom : existing?.MaxZoom ?? 15 };
            saved = saved with { ImageAddedBy = replaceImage ? input.ImageAddedBy : existing?.ImageAddedBy, ImageAddedByUid = replaceImage ? input.ImageAddedByUid : existing?.ImageAddedByUid };
            points[id] = saved;
            try { PersistLocked(); }
            catch { if (existing != null) points[id] = existing; else points.TryRemove(id, out _); throw; }
            return SaveResult.Saved;
        }
    }
    public bool Remove(string id, string ownerUid, bool canManageAll)
    {
        lock (gate)
        {
            if (!points.TryGetValue(id, out var point) || point.OwnerUid != ownerUid && !canManageAll) return false;
            if (!points.TryRemove(id, out _)) return false;
            try { PersistLocked(); } catch { points[id] = point; throw; }
            return true;
        }
    }
    private void PersistLocked() => AtomicFile.Replace(path, temp => File.WriteAllText(temp, JsonSerializer.Serialize(points.Values.OrderBy(p => p.Name), new JsonSerializerOptions { WriteIndented = true })));
    private static string Limit(string? value, int max, string fallback) { value = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim(); return value.Length <= max ? value : value[..max]; }
}
