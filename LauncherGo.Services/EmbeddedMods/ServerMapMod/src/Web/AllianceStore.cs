using System.Text.Json;
using ServerMap.Util;

namespace ServerMap.Web;

/// <summary>World-scoped, symmetric exploration sharing by a privately exchanged map ID.</summary>
public sealed class AllianceStore : IDisposable
{
    public const int MaxAllies = 64;
    private sealed class Entry
    {
        public string? MapId { get; set; }
        public string? Avatar { get; set; }
        public HashSet<string> Allies { get; set; } = new(StringComparer.Ordinal);
        // Old Incoming/Outgoing JSON is intentionally ignored: pending invitations
        // are not consent to join. Already accepted Allies are preserved.
        public Entry Clone() => new() { MapId = MapId, Avatar = Avatar, Allies = new(Allies, StringComparer.Ordinal) };
    }
    private readonly string path;
    private readonly object gate = new();
    private Dictionary<string, Entry> entries;
    private bool disposed;

    public AllianceStore(string root)
    {
        path = Path.Combine(root, "alliances.json");
        entries = File.Exists(path) ? JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(path)) ?? throw new InvalidDataException("Invalid alliances") : new(StringComparer.Ordinal);
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries.Values)
            if (entry == null || entry.Allies == null || entry.MapId != null && (!ValidId(entry.MapId) || !ids.Add(entry.MapId))) throw new InvalidDataException("Invalid map player IDs");
    }
    private static bool ValidId(string id) => id.Length == 32 && id.All(char.IsAsciiHexDigit);
    public bool IsAllied(string owner, string other)
    {
        lock (gate) return entries.GetValueOrDefault(owner)?.Allies.Contains(other) == true;
    }
    public (string MapId, string[] Allies) Snapshot(string owner)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (string.IsNullOrWhiteSpace(owner)) throw new ArgumentException("Invalid player");
            if (!entries.TryGetValue(owner, out var entry) || entry.MapId == null)
            {
                var next = Clone(); entry = Get(next, owner);
                do { entry.MapId = Guid.NewGuid().ToString("N"); } while (entries.Values.Any(e => e.MapId == entry.MapId));
                Commit(next);
            }
            return (entry.MapId!, entry.Allies.ToArray());
        }
    }
    public void Join(string owner, string mapId)
    {
        mapId = mapId.Trim();
        if (!ValidId(mapId)) throw new ArgumentException("Invalid player map ID");
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var target = entries.FirstOrDefault(e => string.Equals(e.Value.MapId, mapId, StringComparison.OrdinalIgnoreCase)).Key ?? throw new KeyNotFoundException("Player map ID not found");
            if (string.IsNullOrWhiteSpace(owner) || owner == target) throw new ArgumentException("Cannot add yourself");
            if (IsAllied(owner, target)) throw new InvalidOperationException("Player already joined");
            if ((entries.GetValueOrDefault(owner)?.Allies.Count ?? 0) >= MaxAllies || entries[target].Allies.Count >= MaxAllies) throw new InvalidOperationException("Player alliance limit reached (64)");
            var next = Clone(); Get(next, owner).Allies.Add(target); Get(next, target).Allies.Add(owner); Commit(next);
        }
    }
    public bool Remove(string owner, string other)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!IsAllied(owner, other)) return false;
            var next = Clone(); next[owner].Allies.Remove(other); next.GetValueOrDefault(other)?.Allies.Remove(owner); Commit(next); return true;
        }
    }
    public string? Avatar(string uid) { lock (gate) return entries.GetValueOrDefault(uid)?.Avatar; }
    public void RememberAvatar(string uid, string key)
    {
        if (key.Length != 64 || !key.All(char.IsAsciiHexDigit)) return;
        lock (gate)
        {
            if (disposed || Avatar(uid) == key) return;
            var next = Clone(); Get(next, uid).Avatar = key; Commit(next);
        }
    }
    private Dictionary<string, Entry> Clone() => entries.ToDictionary(p => p.Key, p => p.Value.Clone(), StringComparer.Ordinal);
    private static Entry Get(Dictionary<string, Entry> state, string uid) => state.GetValueOrDefault(uid) ?? (state[uid] = new());
    private void Commit(Dictionary<string, Entry> next)
    {
        AtomicFile.Replace(path, temp => File.WriteAllText(temp, JsonSerializer.Serialize(next))); entries = next;
    }
    public void Dispose() { lock (gate) disposed = true; }
}
