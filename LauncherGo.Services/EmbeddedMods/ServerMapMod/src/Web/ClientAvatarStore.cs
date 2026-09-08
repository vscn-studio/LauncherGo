using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ServerMap.Render;
using ServerMap.Util;

namespace ServerMap.Web;

/// <summary>Only solicited, sender-owned head snapshots are accepted; rendering is single-worker.</summary>
public sealed class ClientAvatarStore : IDisposable
{
    public const int ChunkSize = 48 * 1024;
    private sealed class Pending(string appearance, string token, long deadline)
    {
        public string Appearance = appearance, Token = token;
        public long Deadline = deadline;
        public int Count, Total;
        public MemoryStream Bytes = new();
        public bool Rendering;
    }
    private sealed record Saved(string Appearance, string Image);
    private readonly object gate = new();
    private readonly Dictionary<string, Pending> pending = new();
    private readonly Dictionary<string, long> retryAt = new();
    private readonly Dictionary<string, string> failures = new();
    private Dictionary<string, Saved> saved = new();
    private readonly string directory;
    private readonly Action<string> log;
    private readonly SemaphoreSlim worker = new(1);
    private readonly CancellationTokenSource stop = new();
    public ClientAvatarStore(string directory, Action<string> log)
    {
        this.directory = directory; this.log = log;
        var path = Path.Combine(directory, "index.json");
        try
        {
            if (File.Exists(path) && new FileInfo(path).Length < 512 * 1024)
                saved = (JsonSerializer.Deserialize<Dictionary<string, Saved>>(File.ReadAllText(path)) ?? new()).Where(p => p.Value is not null && ValidKey(p.Value.Image) && File.Exists(Path.Combine(directory, p.Value.Image + ".png"))).Take(512).ToDictionary();
        }
        catch (Exception ex) { log("Avatar index could not be loaded: " + ex.Message); }
    }
    private static bool ValidKey(string? key) => key?.Length == 64 && key.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    public static string AppearanceKey(string uid, byte[] skin) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("client-head-v3-front/" + uid + "/").Concat(skin).ToArray()));
    public string? GetKey(string uid, string appearance) { lock (gate) return saved.TryGetValue(uid, out var entry) && entry.Appearance == appearance ? entry.Image : null; }
    public string GetStatus(string uid, string? appearance)
    {
        lock (gate)
        {
            if (appearance == null) return "waiting-appearance";
            if (GetKey(uid, appearance) != null) return "ready";
            if (failures.TryGetValue(uid, out var failure)) return failure;
            if (pending.TryGetValue(uid, out var entry)) return entry.Rendering ? "rendering" : entry.Count > 0 ? "receiving" : "waiting-model";
            return retryAt.ContainsKey(uid) ? "retrying" : "waiting-client";
        }
    }
    public string? Request(string uid, string appearance, long now, bool refresh = false)
    {
        lock (gate)
        {
            foreach (var pair in pending.Where(p => p.Value.Deadline < now && !p.Value.Rendering).ToArray())
            {
                log($"Avatar transfer expired. Player={pair.Key}, received={pair.Value.Count}/{pair.Value.Total} chunks.");
                pair.Value.Bytes.Dispose(); pending.Remove(pair.Key);
            }
            if (stop.IsCancellationRequested || !refresh && GetKey(uid, appearance) != null || retryAt.GetValueOrDefault(uid) > now || pending.ContainsKey(uid) || pending.Count >= 8 || saved.Count >= 512 && !saved.ContainsKey(uid)) return null;
            var token = Guid.NewGuid().ToString("N"); pending[uid] = new(appearance, token, now + 90_000); retryAt[uid] = now + 120_000;
            log($"Avatar head mesh requested. Player={uid}."); return token;
        }
    }
    public bool ReportFailure(string uid, string token, string error, long now)
    {
        lock (gate)
        {
            if (stop.IsCancellationRequested || !pending.TryGetValue(uid, out var entry) || entry.Token != token || entry.Rendering || now > entry.Deadline ||
                error is not ("capture-failed" or "model-timeout" or "packing-failed" or "appearance-changed")) return false;
            entry.Bytes.Dispose(); pending.Remove(uid); failures[uid] = error;
            // Preserve the existing retry rate limit, but expose the real failure immediately.
            log($"Avatar client failure. Player={uid}, stage={error}."); return true;
        }
    }
    public bool Receive(string uid, string token, int index, int total, byte[] data, long now)
    {
        lock (gate)
        {
            if (stop.IsCancellationRequested || !pending.TryGetValue(uid, out var entry) || entry.Token != token || entry.Rendering || now > entry.Deadline) return false;
            if (index != entry.Count || total is < 1 or > 64 || entry.Count > 0 && total != entry.Total || data is not { Length: > 0 and <= ChunkSize } || entry.Bytes.Length + data.Length > AvatarScene.MaxBytes)
            { entry.Bytes.Dispose(); pending.Remove(uid); return false; }
            entry.Total = total; entry.Count++; entry.Bytes.Write(data);
            failures.Remove(uid);
            if (entry.Count != total) return true;
            entry.Rendering = true; var bytes = entry.Bytes.ToArray(); entry.Bytes.Dispose();
            _ = Generate(uid, entry, bytes); return true;
        }
    }
    private async Task Generate(string uid, Pending entry, byte[] bytes)
    {
        try
        {
            await Task.Run(async () =>
            {
                await worker.WaitAsync(stop.Token);
                try
                {
                    stop.Token.ThrowIfCancellationRequested(); var png = AvatarScene.Unpack(bytes).Render(stop.Token);
                    var key = Convert.ToHexStringLower(SHA256.HashData(png));
                    stop.Token.ThrowIfCancellationRequested();
                    Dictionary<string, Saved> next;
                    lock (gate) next = new Dictionary<string, Saved>(saved) { [uid] = new(entry.Appearance, key) };
                    AtomicFile.Replace(Path.Combine(directory, key + ".png"), temp => File.WriteAllBytes(temp, png));
                    stop.Token.ThrowIfCancellationRequested();
                    AtomicFile.Replace(Path.Combine(directory, "index.json"), temp => File.WriteAllText(temp, JsonSerializer.Serialize(next)));
                    lock (gate) saved = next;
                    log($"Client-model avatar generated and cached. Player={uid}, PNG={png.Length} bytes.");
                }
                finally { worker.Release(); }
            }, stop.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { log($"Client-model avatar failed. Player={uid}: {ex.Message}"); }
        finally { lock (gate) if (pending.GetValueOrDefault(uid) == entry) pending.Remove(uid); }
    }
    public byte[]? Get(string key)
    {
        if (!ValidKey(key)) return null;
        lock (gate) if (!saved.Values.Any(v => v.Image == key)) return null;
        var path = Path.Combine(directory, key + ".png");
        try { return File.Exists(path) && new FileInfo(path).Length <= LocalAvatarRenderer.MaxImageBytes ? File.ReadAllBytes(path) : null; }
        catch (IOException) { return null; }
    }
    public void ForgetConnection(string uid) { lock (gate) { if (pending.Remove(uid, out var entry) && !entry.Rendering) entry.Bytes.Dispose(); retryAt.Remove(uid); failures.Remove(uid); } }
    public void Dispose() { stop.Cancel(); lock (gate) { foreach (var entry in pending.Values) entry.Bytes.Dispose(); pending.Clear(); } }
}
