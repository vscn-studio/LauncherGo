using System.Collections.Concurrent;
using ServerMap.Util;

namespace ServerMap.Web;

/// <summary>Bounded, single-worker generation. HTTP/game snapshot callers never compose an image.</summary>
public sealed class LocalAvatarCache : IDisposable
{
    private sealed class Entry { public byte[]? Bytes; public long RetryAt; }
    private readonly ConcurrentDictionary<string, Entry> entries = new();
    private readonly LocalAvatarRenderer renderer;
    private readonly string directory, revision;
    private readonly Action<string> log;
    private readonly SemaphoreSlim worker = new(1, 1);
    private readonly CancellationTokenSource stop = new();
    private readonly object gate = new();
    public LocalAvatarCache(LocalAvatarRenderer renderer, string directory, string revision, Action<string> log)
    { this.renderer = renderer; this.directory = directory; this.revision = revision; this.log = log; }

    public string? Request(LocalAvatarRenderer.Appearance appearance)
    {
        if (!appearance.Valid || stop.IsCancellationRequested) return null;
        var key = appearance.Key(revision);
        lock (gate)
        {
            if (!entries.TryGetValue(key, out var entry))
            {
                if (entries.Count >= 512) return null;
                entry = new Entry(); entries[key] = entry;
            }
            if (entry.Bytes != null) return key;
            if (entry.RetryAt > Environment.TickCount64) return null;
            entry.RetryAt = long.MaxValue;
            _ = Generate(key, appearance, entry);
            return null;
        }
    }

    private async Task Generate(string key, LocalAvatarRenderer.Appearance appearance, Entry entry)
    {
        try
        {
            await Task.Run(async () =>
            {
                await worker.WaitAsync(stop.Token);
                try
                {
                    stop.Token.ThrowIfCancellationRequested();
                    var file = Path.Combine(directory, key + ".png");
                    byte[] bytes;
                    if (File.Exists(file) && new FileInfo(file).Length <= LocalAvatarRenderer.MaxImageBytes)
                        bytes = File.ReadAllBytes(file);
                    else
                    {
                        bytes = renderer.Render(appearance);
                        stop.Token.ThrowIfCancellationRequested();
                        AtomicFile.Replace(file, temporary => File.WriteAllBytes(temporary, bytes));
                    }
                    lock (gate) entry.Bytes = bytes;
                }
                finally { worker.Release(); }
            }, stop.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            lock (gate) entry.RetryAt = Environment.TickCount64 + 60000;
            log("Local avatar generation unavailable: " + ex.Message);
        }
    }
    public byte[]? Get(string key)
    {
        lock (gate) return entries.TryGetValue(key, out var entry) ? entry.Bytes?.ToArray() : null;
    }
    public void Dispose() => stop.Cancel();
}
