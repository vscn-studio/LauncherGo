using System.Security.Cryptography;

namespace ServerMap.Web;

/// <summary>Game-thread mount snapshots and bounded, solicited rider-owned image uploads.</summary>
public sealed class MountSnapshotStore(Func<byte[], byte[]> normalizeImage) : IDisposable
{
    public const int ChunkSize = 48 * 1024, MaxBytes = 4 * 1024 * 1024, MaxChunks = (MaxBytes + ChunkSize - 1) / ChunkSize;
    public sealed record Rider(string Uid, string Name, double X, double Z);
    public sealed record Mount(long Id, string Code, string Name, double X, double Y, double Z, float Yaw, Rider[] Riders);
    public sealed record Image(string Key, byte[] Png, double WorldSize, double CenterX, double CenterZ, long Created, double Footprint);
    public static double DisplayScale(double footprint) => 2 - Math.Clamp((footprint - 4) / 4, 0, 1);
    private sealed class Pending(string uid, long id, string token, long deadline)
    {
        public string Uid = uid, Token = token; public long Id = id, Deadline = deadline;
        public int Count, Total; public bool Rendering; public double Size, X, Z, Footprint;
        public MemoryStream Bytes = new();
    }
    private readonly object gate = new();
    private Mount[] active = [];
    private readonly Dictionary<long, Image> images = new();
    private readonly Dictionary<long, Pending> pending = new();
    private readonly Dictionary<long, long> retry = new();
    private readonly CancellationTokenSource stop = new();
    private readonly SemaphoreSlim worker = new(1);
    public Mount[] Active { get { lock (gate) return active.ToArray(); } }
    public void Replace(IEnumerable<Mount> mounts, long now)
    {
        lock (gate)
        {
            active = mounts.Take(512).ToArray();
            foreach (var entry in pending.Values.Where(p => !Owns(p.Uid, p.Id) || now > p.Deadline).ToArray())
            { pending.Remove(entry.Id); entry.Bytes.Dispose(); }
            foreach (var id in retry.Keys.Where(id => !active.Any(m => m.Id == id)).ToArray()) retry.Remove(id);
        }
    }
    private bool Owns(string uid, long id) => active.Any(m => m.Id == id && m.Riders.Any(r => r.Uid == uid));
    public Image? Get(long id) { lock (gate) return images.GetValueOrDefault(id); }
    public string? Request(string uid, long id, long now)
    {
        lock (gate)
        {
            if (stop.IsCancellationRequested || !Owns(uid, id) || pending.ContainsKey(id) || pending.Count >= 4 || retry.GetValueOrDefault(id) > now
                || images.TryGetValue(id, out var image) && now - image.Created < 120_000) return null;
            var token = Guid.NewGuid().ToString("N"); pending[id] = new(uid, id, token, now + 60_000); retry[id] = now + 60_000; return token;
        }
    }
    public bool Fail(string uid, long id, string token)
    {
        lock (gate)
        {
            if (!pending.TryGetValue(id, out var p) || p.Uid != uid || p.Token != token) return false;
            pending.Remove(id); p.Bytes.Dispose(); return true;
        }
    }
    public bool Receive(string uid, long id, string token, int index, int total, byte[] bytes, double size, double x, double z, long now, double footprint)
    {
        lock (gate)
        {
            if (stop.IsCancellationRequested || !Owns(uid, id) || !pending.TryGetValue(id, out var p) || p.Uid != uid || p.Token != token || p.Rendering || now > p.Deadline) return false;
            if (index != p.Count || total is < 1 or > MaxChunks || p.Count > 0 && (p.Total != total || p.Size != size || p.X != x || p.Z != z || p.Footprint != footprint)
                || bytes is not { Length: > 0 and <= ChunkSize } || p.Bytes.Length + bytes.Length > MaxBytes
                || !double.IsFinite(size + x + z + footprint) || size is < .01 or > 320 || footprint is < .01 or > 192 || Math.Abs(x) > 100 || Math.Abs(z) > 100)
            { pending.Remove(id); p.Bytes.Dispose(); return false; }
            p.Total = total; p.Size = size; p.X = x; p.Z = z; p.Footprint = footprint; p.Count++; p.Bytes.Write(bytes);
            if (p.Count != total) return true;
            p.Rendering = true; var data = p.Bytes.ToArray(); p.Bytes.Dispose(); _ = Generate(p, data); return true;
        }
    }
    private async Task Generate(Pending p, byte[] bytes)
    {
        try
        {
            await Task.Run(async () =>
            {
                await worker.WaitAsync(stop.Token);
                try
                {
                    stop.Token.ThrowIfCancellationRequested(); var png = normalizeImage(bytes);
                    if (png.Length > MaxBytes) throw new InvalidDataException("Mount PNG too large");
                    var key = Convert.ToHexStringLower(SHA256.HashData(png));
                    lock (gate)
                    {
                        if (stop.IsCancellationRequested || pending.GetValueOrDefault(p.Id) != p || !Owns(p.Uid, p.Id)) return;
                        images[p.Id] = new(key, png, p.Size, p.X, p.Z, Environment.TickCount64, p.Footprint);
                        while (images.Count > 32 || images.Values.Sum(v => (long)v.Png.Length) > 32 * 1024 * 1024)
                            images.Remove(images.MinBy(v => v.Value.Created).Key);
                    }
                }
                finally { worker.Release(); }
            }, stop.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception) { /* Invalid client snapshots are discarded, never served verbatim. */ }
        finally { lock (gate) if (pending.GetValueOrDefault(p.Id) == p) pending.Remove(p.Id); }
    }
    public void Dispose() { stop.Cancel(); lock (gate) { foreach (var p in pending.Values) p.Bytes.Dispose(); pending.Clear(); images.Clear(); active = []; } }
}
