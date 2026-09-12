using System.Text.Json;
using ServerMap.Util;

namespace ServerMap.Web;

/// <summary>Persistent per-player reactions for Spotlight posts.</summary>
public sealed class MomentLikeStore
{
    private readonly string path;
    private readonly object gate = new();
    private Dictionary<string, HashSet<string>> likes = new(StringComparer.Ordinal);

    public MomentLikeStore(string root)
    {
        path = Path.Combine(root, "moment-likes.json");
        try
        {
            if (!File.Exists(path)) return;
            var loaded = JsonSerializer.Deserialize<Dictionary<string, string[]>>(File.ReadAllText(path));
            if (loaded != null)
                likes = loaded.ToDictionary(p => p.Key, p => new HashSet<string>(p.Value ?? [], StringComparer.Ordinal), StringComparer.Ordinal);
        }
        catch { likes = new(StringComparer.Ordinal); }
    }

    public int Count(string momentId)
    {
        lock (gate) return likes.GetValueOrDefault(momentId)?.Count ?? 0;
    }

    public bool HasLiked(string momentId, string? playerUid)
    {
        if (string.IsNullOrWhiteSpace(playerUid)) return false;
        lock (gate) return likes.GetValueOrDefault(momentId)?.Contains(playerUid) == true;
    }

    public (bool Liked, int Count) Toggle(string momentId, string playerUid)
    {
        if (string.IsNullOrWhiteSpace(momentId) || string.IsNullOrWhiteSpace(playerUid)) throw new ArgumentException("Invalid moment like");
        lock (gate)
        {
            return SetLocked(momentId, playerUid, !(likes.GetValueOrDefault(momentId)?.Contains(playerUid) == true));
        }
    }

    public (bool Liked, int Count) Set(string momentId, string playerUid, bool liked)
    {
        if (string.IsNullOrWhiteSpace(momentId) || string.IsNullOrWhiteSpace(playerUid)) throw new ArgumentException("Invalid moment like");
        lock (gate) return SetLocked(momentId, playerUid, liked);
    }

    private (bool Liked, int Count) SetLocked(string momentId, string playerUid, bool liked)
    {
        var current = likes.GetValueOrDefault(momentId)?.Contains(playerUid) == true;
        if (current == liked) return (liked, likes.GetValueOrDefault(momentId)?.Count ?? 0);
        var next = likes.ToDictionary(p => p.Key, p => new HashSet<string>(p.Value, StringComparer.Ordinal), StringComparer.Ordinal);
        var set = next.GetValueOrDefault(momentId) ?? (next[momentId] = new HashSet<string>(StringComparer.Ordinal));
        if (liked) set.Add(playerUid); else set.Remove(playerUid);
        if (set.Count == 0) next.Remove(momentId);
        AtomicFile.Replace(path, temp => File.WriteAllText(temp, JsonSerializer.Serialize(next.ToDictionary(p => p.Key, p => p.Value.ToArray()))));
        likes = next;
        return (liked, set.Count);
    }

    public void Clear(string momentId)
    {
        if (string.IsNullOrWhiteSpace(momentId)) return;
        lock (gate)
        {
            if (!likes.ContainsKey(momentId)) return;
            var next = likes.ToDictionary(p => p.Key, p => new HashSet<string>(p.Value, StringComparer.Ordinal), StringComparer.Ordinal);
            next.Remove(momentId);
            try { AtomicFile.Replace(path, temp => File.WriteAllText(temp, JsonSerializer.Serialize(next.ToDictionary(p => p.Key, p => p.Value.ToArray())))); }
            catch { /* A stale reaction must never prevent a marker/image update. */ }
            likes = next;
        }
    }
}
