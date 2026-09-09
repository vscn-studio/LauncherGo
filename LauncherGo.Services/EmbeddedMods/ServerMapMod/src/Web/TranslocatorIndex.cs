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
    public bool Restored { get; private set; }
    public TranslocatorIndex(string path, Action<string> warn)
    {
        this.path = path;
        try
        {
            if (!File.Exists(path)) { dirty = true; return; }
            foreach (var group in (JsonSerializer.Deserialize<TranslocatorPoint[]>(File.ReadAllText(path)) ?? [])
                .GroupBy(p => (p.X >> 5, p.Y >> 5, p.Z >> 5))) chunks[group.Key] = group.ToArray();
            Restored = true;
        }
        catch (Exception ex) { dirty = true; warn($"ServerMap translocator cache could not be restored: {ex.Message}"); }
    }
    public TranslocatorPoint[] Values
    {
        get
        {
            lock (gate)
            {
                // Older indexes could contain the same endpoint once per
                // scanned chunk. The endpoint pair is the identity exposed to
                // the web layer; collapse duplicates before serialization or
                // rendering so two links cannot become thousands of markers.
                return chunks.Values.SelectMany(v => v)
                    .GroupBy(p => (p.X, p.Y, p.Z, p.TargetX, p.TargetY, p.TargetZ))
                    .Select(g => g.First())
                    .OrderBy(p => p.Id, StringComparer.Ordinal)
                    .ToArray();
            }
        }
    }
    public int Count => Values.Length;
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
            AtomicFile.Replace(path, temp => File.WriteAllText(temp, JsonSerializer.Serialize(Values)));
            dirty = false;
        }
    }
}
