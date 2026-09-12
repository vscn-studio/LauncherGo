using System.Text.Json;
using ServerMap.Util;

namespace ServerMap.Web;

/// <summary>World-scoped, symmetric exploration sharing by a privately exchanged map ID.</summary>
public sealed class AllianceStore : IDisposable
{
    public const int MaxAllies = 64;
    public static readonly TimeSpan RequestLifetime = TimeSpan.FromMinutes(10);

    public sealed record Pending(string Uid, bool Incoming, DateTimeOffset ExpiresAt);

    private sealed class Entry
    {
        public string? MapId { get; set; }
        public string? Avatar { get; set; }
        public HashSet<string> Allies { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, DateTimeOffset> PendingOutgoing { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, DateTimeOffset> PendingIncoming { get; set; } = new(StringComparer.Ordinal);
        // Older Incoming/Outgoing invitation fields are intentionally ignored.
        // They were never accepted by the previous implementation.
        public Entry Clone() => new()
        {
            MapId = MapId,
            Avatar = Avatar,
            Allies = new(Allies, StringComparer.Ordinal),
            PendingOutgoing = new(PendingOutgoing, StringComparer.Ordinal),
            PendingIncoming = new(PendingIncoming, StringComparer.Ordinal)
        };
    }

    private readonly string path;
    private readonly object gate = new();
    private readonly TimeProvider clock;
    private Dictionary<string, Entry> entries;
    private DateTimeOffset nextExpiry = DateTimeOffset.MaxValue;
    private bool disposed;

    public AllianceStore(string root, TimeProvider? clock = null)
    {
        this.clock = clock ?? TimeProvider.System;
        path = Path.Combine(root, "alliances.json");
        entries = File.Exists(path) ? JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(path)) ?? throw new InvalidDataException("Invalid alliances") : new(StringComparer.Ordinal);
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries.Values)
        {
            if (entry == null || entry.Allies == null || entry.MapId != null && (!ValidId(entry.MapId) || !ids.Add(entry.MapId))) throw new InvalidDataException("Invalid map player IDs");
            entry.PendingOutgoing ??= new(StringComparer.Ordinal);
            entry.PendingIncoming ??= new(StringComparer.Ordinal);
        }
        UpdateNextExpiry();
        PruneExpiredLocked(this.clock.GetUtcNow());
    }

    private static bool ValidId(string id) => id.Length == 32 && id.All(char.IsAsciiHexDigit);

    public bool IsAllied(string owner, string other)
    {
        lock (gate) return entries.GetValueOrDefault(owner)?.Allies.Contains(other) == true;
    }

    public bool PruneExpired()
    {
        lock (gate) return !disposed && PruneExpiredLocked(clock.GetUtcNow());
    }

    public (string MapId, string[] Allies) Snapshot(string owner)
    {
        var details = SnapshotDetails(owner);
        return (details.MapId, details.Allies);
    }

    public (string MapId, string[] Allies, Pending[] Pending) SnapshotDetails(string owner)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (string.IsNullOrWhiteSpace(owner)) throw new ArgumentException("Invalid player");
            PruneExpiredLocked(clock.GetUtcNow());
            if (!entries.TryGetValue(owner, out var entry) || entry.MapId == null)
            {
                var next = Clone(); entry = Get(next, owner);
                do { entry.MapId = Guid.NewGuid().ToString("N"); } while (entries.Values.Any(e => e.MapId == entry.MapId));
                Commit(next);
            }
            var pending = entry.PendingIncoming.Select(p => new Pending(p.Key, true, p.Value))
                .Concat(entry.PendingOutgoing.Select(p => new Pending(p.Key, false, p.Value)))
                .OrderBy(p => p.ExpiresAt).ToArray();
            return (entry.MapId!, entry.Allies.ToArray(), pending);
        }
    }

    /// <summary>Creates a pending request. The recipient must call <see cref="Accept"/>.</summary>
    public void Request(string owner, string mapId)
    {
        mapId = mapId.Trim();
        if (!ValidId(mapId)) throw new ArgumentException("Invalid player map ID");
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var now = clock.GetUtcNow();
            PruneExpiredLocked(now);
            if (string.IsNullOrWhiteSpace(owner)) throw new ArgumentException("Invalid player");
            var target = entries.FirstOrDefault(e => string.Equals(e.Value.MapId, mapId, StringComparison.OrdinalIgnoreCase)).Key ?? throw new KeyNotFoundException("Player map ID not found");
            var sourceEntry = entries.GetValueOrDefault(owner) ?? new Entry();
            var targetEntry = entries[target];
            if (owner == target) throw new ArgumentException("Cannot add yourself");
            if (sourceEntry.Allies.Contains(target)) throw new InvalidOperationException("Player already joined");
            if (sourceEntry.PendingOutgoing.ContainsKey(target) || sourceEntry.PendingIncoming.ContainsKey(target)) throw new InvalidOperationException("Player request pending");
            if (sourceEntry.Allies.Count >= MaxAllies || targetEntry.Allies.Count >= MaxAllies) throw new InvalidOperationException("Player alliance limit reached (64)");
            var next = Clone();
            Get(next, owner).PendingOutgoing[target] = now.Add(RequestLifetime);
            next[target].PendingIncoming[owner] = next[owner].PendingOutgoing[target];
            Commit(next);
        }
    }

    public void Accept(string owner, string other)
    {
        if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(other) || owner == other) throw new ArgumentException("Invalid player");
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            PruneExpiredLocked(clock.GetUtcNow());
            if (!entries.TryGetValue(owner, out var source) || !source.PendingIncoming.ContainsKey(other)) throw new KeyNotFoundException("Player request not found");
            if (!entries.TryGetValue(other, out var target) || !target.PendingOutgoing.ContainsKey(owner)) throw new KeyNotFoundException("Player request not found");
            if (source.Allies.Contains(other)) throw new InvalidOperationException("Player already joined");
            if (source.Allies.Count >= MaxAllies || target.Allies.Count >= MaxAllies) throw new InvalidOperationException("Player alliance limit reached (64)");
            var next = Clone();
            next[owner].PendingIncoming.Remove(other);
            next[other].PendingOutgoing.Remove(owner);
            next[owner].Allies.Add(other);
            next[other].Allies.Add(owner);
            Commit(next);
        }
    }

    /// <summary>Legacy direct join kept for migration and non-web callers.</summary>
    public void Join(string owner, string mapId)
    {
        mapId = mapId.Trim();
        if (!ValidId(mapId)) throw new ArgumentException("Invalid player map ID");
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            PruneExpiredLocked(clock.GetUtcNow());
            var target = entries.FirstOrDefault(e => string.Equals(e.Value.MapId, mapId, StringComparison.OrdinalIgnoreCase)).Key ?? throw new KeyNotFoundException("Player map ID not found");
            if (string.IsNullOrWhiteSpace(owner) || owner == target) throw new ArgumentException("Cannot add yourself");
            var sourceEntry = entries.GetValueOrDefault(owner) ?? new Entry();
            if (sourceEntry.Allies.Contains(target)) throw new InvalidOperationException("Player already joined");
            if (sourceEntry.Allies.Count >= MaxAllies || entries[target].Allies.Count >= MaxAllies) throw new InvalidOperationException("Player alliance limit reached (64)");
            var next = Clone();
            Get(next, owner).PendingOutgoing.Remove(target);
            Get(next, owner).PendingIncoming.Remove(target);
            next[target].PendingIncoming.Remove(owner);
            next[target].PendingOutgoing.Remove(owner);
            Get(next, owner).Allies.Add(target);
            next[target].Allies.Add(owner);
            Commit(next);
        }
    }

    public bool Remove(string owner, string other)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            PruneExpiredLocked(clock.GetUtcNow());
            if (!entries.TryGetValue(owner, out var source) || !source.Allies.Contains(other)) return false;
            var next = Clone();
            next[owner].Allies.Remove(other);
            next.GetValueOrDefault(other)?.Allies.Remove(owner);
            Commit(next); return true;
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

    private bool PruneExpiredLocked(DateTimeOffset now)
    {
        if (now < nextExpiry) return false;
        var expired = new List<(string Owner, string Other)>();
        foreach (var (owner, entry) in entries)
        {
            foreach (var item in entry.PendingOutgoing) if (item.Value <= now) expired.Add((owner, item.Key));
            foreach (var item in entry.PendingIncoming) if (item.Value <= now) expired.Add((item.Key, owner));
        }
        if (expired.Count == 0) return false;
        var next = Clone();
        foreach (var (owner, other) in expired)
        {
            next.GetValueOrDefault(owner)?.PendingOutgoing.Remove(other);
            next.GetValueOrDefault(other)?.PendingIncoming.Remove(owner);
        }
        Commit(next); return true;
    }

    private Dictionary<string, Entry> Clone() => entries.ToDictionary(p => p.Key, p => p.Value.Clone(), StringComparer.Ordinal);
    private static Entry Get(Dictionary<string, Entry> state, string uid) => state.GetValueOrDefault(uid) ?? (state[uid] = new());
    private void Commit(Dictionary<string, Entry> next)
    {
        AtomicFile.Replace(path, temp => File.WriteAllText(temp, JsonSerializer.Serialize(next))); entries = next; UpdateNextExpiry();
    }
    private void UpdateNextExpiry() => nextExpiry = entries.Values.SelectMany(e => e.PendingIncoming.Values.Concat(e.PendingOutgoing.Values)).DefaultIfEmpty(DateTimeOffset.MaxValue).Min();
    public void Dispose() { lock (gate) disposed = true; }
}
