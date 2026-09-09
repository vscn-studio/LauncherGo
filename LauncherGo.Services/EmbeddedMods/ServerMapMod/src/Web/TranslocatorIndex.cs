using System.Text.Json;
using ServerMap.Util;

namespace ServerMap.Web;

public sealed record TranslocatorPoint(int X, int Y, int Z, int TargetX, int TargetY, int TargetZ)
{
    public string Id => $"translocator-{X}-{Y}-{Z}";
    public string Name => "Translocator";
    public string Kind => "translocator";
}

/// <summary>Only a successfully read saved chunk may replace its previous entries.</summary>
public sealed class TranslocatorIndex
{
    private readonly string path;
    private readonly object gate = new();
    private readonly Dictionary<(int X, int Y, int Z), TranslocatorPoint[]> chunks = [];
    private bool dirty;
    public TranslocatorIndex(string path, Action<string> warn)
    {
        this.path = path;
        try
        {
            if (!File.Exists(path)) return;
            foreach (var group in (JsonSerializer.Deserialize<TranslocatorPoint[]>(File.ReadAllText(path)) ?? [])
                .GroupBy(p => (p.X >> 5, p.Y >> 5, p.Z >> 5))) chunks[group.Key] = group.ToArray();
        }
        catch (Exception ex) { warn($"ServerMap translocator cache could not be restored: {ex.Message}"); }
    }
    public TranslocatorPoint[] Values { get { lock (gate) return chunks.Values.SelectMany(v => v).ToArray(); } }
    public int Count { get { lock (gate) return chunks.Values.Sum(v => v.Length); } }
    public bool ReplaceChunk(int x, int y, int z, IEnumerable<TranslocatorPoint> points)
    {
        var next = points.Where(p => (p.X >> 5, p.Y >> 5, p.Z >> 5) == (x, y, z)).OrderBy(p => p.Id, StringComparer.Ordinal).ToArray();
        lock (gate)
        {
            var key = (x, y, z);
            var previous = chunks.GetValueOrDefault(key) ?? [];
            if (previous.SequenceEqual(next)) return false;
            if (next.Length == 0) chunks.Remove(key); else chunks[key] = next;
            dirty = true;
            return true;
        }
    }
    public void Save()
    {
        lock (gate)
        {
            if (!dirty) return;
            AtomicFile.Replace(path, temp => File.WriteAllText(temp, JsonSerializer.Serialize(chunks.Values.SelectMany(v => v))));
            dirty = false;
        }
    }
}
