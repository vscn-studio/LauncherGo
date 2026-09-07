using System.Collections.Concurrent;
using System.Text.Json;
using ServerMap.Util;

namespace ServerMap.Web;

public sealed class PoiStore
{
    public enum SaveResult { Saved, QuotaExceeded, Forbidden }
    public sealed record Poi(string Id, string Type, string Name, string Text, string Color, double Rotation, double X, double Z, double? X2, double? Z2, string OwnerUid, DateTimeOffset UpdatedAt);
    private readonly string path; private readonly object gate = new(); private readonly ConcurrentDictionary<string, Poi> points = new();
    public PoiStore(string path) { this.path = path; try { if (File.Exists(path)) foreach (var point in JsonSerializer.Deserialize<Poi[]>(File.ReadAllText(path)) ?? []) points[point.Id] = point; } catch { } }
    public IReadOnlyCollection<Poi> All => points.Values.ToArray();
    public SaveResult TrySave(Poi input, string ownerUid, int maxPerOwner, bool canManageAll, out Poi? saved)
    {
        lock (gate)
        {
            Poi? existing = null;
            var updating = !string.IsNullOrWhiteSpace(input.Id) && points.TryGetValue(input.Id, out existing);
            if (updating && existing!.OwnerUid != ownerUid && !canManageAll) { saved = null; return SaveResult.Forbidden; }
            if (!updating && points.Values.Count(point => point.OwnerUid == ownerUid) >= Math.Max(0, maxPerOwner)) { saved = null; return SaveResult.QuotaExceeded; }
            var type = input.Type is "rectangle" or "text" ? input.Type : "point";
            var inputColor = input.Color ?? "";
            var color = System.Text.RegularExpressions.Regex.IsMatch(inputColor, "^#[0-9a-fA-F]{6}$") ? inputColor : "#e66c75";
            var rotation = double.IsFinite(input.Rotation) ? (input.Rotation % 360 + 360) % 360 : 0;
            if (rotation > 180) rotation -= 360;
            var id = updating ? existing!.Id : Guid.NewGuid().ToString("N");
            var persistedOwner = updating ? existing!.OwnerUid : ownerUid;
            saved = new Poi(id, type, Limit(input.Name, 80, "POI"), Limit(input.Text, 500, ""), color, rotation, input.X, input.Z, input.X2, input.Z2, persistedOwner, DateTimeOffset.UtcNow);
            points[id] = saved; PersistLocked(); return SaveResult.Saved;
        }
    }
    public bool Remove(string id, string ownerUid, bool canManageAll)
    {
        lock (gate)
        {
            if (!points.TryGetValue(id, out var point) || point.OwnerUid != ownerUid && !canManageAll) return false;
            if (!points.TryRemove(id, out _)) return false;
            PersistLocked(); return true;
        }
    }
    private void PersistLocked() => AtomicFile.Replace(path, temp => File.WriteAllText(temp, JsonSerializer.Serialize(points.Values.OrderBy(p => p.Name), new JsonSerializerOptions { WriteIndented = true })));
    private static string Limit(string? value, int max, string fallback) { value = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim(); return value.Length <= max ? value : value[..max]; }
}
