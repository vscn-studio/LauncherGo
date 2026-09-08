using System.Collections.Concurrent;
using System.Threading.Channels;

namespace ServerMap.Util;

// Jobs can be queued during startup, but none execute until RunGame starts us.
internal sealed class BackgroundMapWork(Action<string, Exception> onError)
{
    private readonly Channel<(string Key, Func<CancellationToken, Task> Run)> jobs = Channel.CreateUnbounded<(string, Func<CancellationToken, Task>)>();
    private readonly ConcurrentDictionary<string, byte> pending = new();
    private readonly CancellationTokenSource stop = new();
    private Task worker = Task.CompletedTask;
    private readonly object gate = new();
    private bool started;
    private bool stopped;

    public void Enqueue(string key, Func<CancellationToken, Task> run)
    {
        if (stop.IsCancellationRequested || !pending.TryAdd(key, 0)) return;
        if (!jobs.Writer.TryWrite((key, run))) pending.TryRemove(key, out _);
    }

    public void Start(TimeSpan delay)
    {
        lock (gate)
        {
            if (started || stopped) return;
            started = true;
            worker = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(delay, stop.Token).ConfigureAwait(false);
                    await foreach (var job in jobs.Reader.ReadAllAsync(stop.Token).ConfigureAwait(false))
                    {
                        stop.Token.ThrowIfCancellationRequested();
                        pending.TryRemove(job.Key, out _);
                        try { await job.Run(stop.Token).ConfigureAwait(false); }
                        catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
                        catch (Exception ex) { onError(job.Key, ex); }
                    }
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            });
        }
    }

    public Task StopAsync()
    {
        lock (gate)
        {
            stopped = true;
            stop.Cancel();
            jobs.Writer.TryComplete();
            return worker;
        }
    }
}
