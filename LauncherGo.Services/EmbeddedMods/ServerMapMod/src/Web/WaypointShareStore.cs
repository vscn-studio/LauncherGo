using System.Text.Json;
using ServerMap.Util;

namespace ServerMap.Web;

public sealed class WaypointShareStore
{
    public sealed record Share(string Id, GameWaypointSnapshot.Marker Snapshot);
    private readonly object gate = new();
    private readonly string path;
    private Share[] shares;
    public WaypointShareStore(string path)
    {
        this.path = path;
        shares = File.Exists(path) ? JsonSerializer.Deserialize<Share[]>(File.ReadAllText(path)) ?? throw new InvalidDataException("Invalid waypoint shares") : [];
    }
    public string Create(string owner, GameWaypointSnapshot.Marker marker)
    {
        if (marker.OwnerUid != owner) throw new UnauthorizedAccessException();
        lock (gate)
        {
            var previous = shares.FirstOrDefault(s => s.Snapshot == marker);
            if (previous != null) return previous.Id;
            if (shares.Count(s => s.Snapshot.OwnerUid == owner) >= 200) throw new InvalidOperationException("Waypoint share limit reached (200)");
            var share = new Share(Guid.NewGuid().ToString("N"), marker);
            Commit([.. shares, share]); return share.Id;
        }
    }
    public GameWaypointSnapshot.Marker? Get(string id) { lock (gate) return shares.FirstOrDefault(s => s.Id == id)?.Snapshot; }
    public void Prune(IEnumerable<GameWaypointSnapshot.Marker> existing)
    {
        var ids = existing.Select(m => (m.OwnerUid, m.Id)).ToHashSet();
        lock (gate)
        {
            var next = shares.Where(s => ids.Contains((s.Snapshot.OwnerUid, s.Snapshot.Id))).ToArray();
            if (next.Length != shares.Length) Commit(next);
        }
    }
    private void Commit(Share[] next)
    {
        AtomicFile.Replace(path, temporary => File.WriteAllText(temporary, JsonSerializer.Serialize(next)));
        shares = next;
    }
}
