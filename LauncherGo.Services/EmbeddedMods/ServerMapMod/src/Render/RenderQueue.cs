using System.Collections.Concurrent;
using ServerMap.World;

namespace ServerMap.Render;

public enum RenderQueueOutcome
{
    Completed,
    RetryLater
}

public sealed class RenderQueue : IDisposable
{
    private const int MaxRetryAttempts = 12;
    private const int FirstRetryDelayMs = 1000;
    private const int MaxRetryDelayMs = 30000;

    private sealed class JobState
    {
        public bool IsRunning;
        public bool RerunRequested;
        public bool RetryScheduled;
        public int RetryAttempts;
        public int RetryGeneration;
        public bool Priority;
    }

    private readonly BlockingCollection<ChunkKey> jobs = new();
    private readonly BlockingCollection<ChunkKey> priorityJobs = new();
    private readonly Dictionary<ChunkKey, JobState> pending = [];
    private readonly object pendingGate = new();
    private readonly CancellationTokenSource stop = new();
    private readonly Task[] workers;
    private readonly Func<ChunkKey, RenderQueueOutcome> render;
    private bool disposed;

    public RenderQueue(int threadCount, Func<ChunkKey, RenderQueueOutcome> render)
    {
        this.render = render;
        workers = Enumerable.Range(0, Math.Clamp(threadCount, 1, 4)).Select(_ => Task.Run(Work)).ToArray();
    }

    public void Enqueue(ChunkKey key, bool priority = false)
    {
        lock (pendingGate)
        {
            if (disposed) return;
            if (pending.TryGetValue(key, out var current))
            {
                if (current.IsRunning)
                {
                    // A save while rendering must result in one fresh pass.
                    current.RerunRequested = true;
                    current.Priority |= priority;
                    return;
                }

                if (current.RetryScheduled)
                {
                    // A save/chunk-load event is stronger evidence than a
                    // delayed retry. Cancel the delay and run immediately.
                    current.RetryScheduled = false;
                    current.RetryAttempts = 0;
                    current.RetryGeneration++;
                    current.Priority |= priority;
                    AddJobLocked(key, current);
                }
                else if (priority && !current.Priority)
                {
                    current.Priority = true;
                    AddJobLocked(key, current);
                }
                // Otherwise the key is already waiting in BlockingCollection.
                return;
            }

            var state = new JobState { Priority = priority };
            pending[key] = state;
            AddJobLocked(key, state);
        }
    }

    private void AddJobLocked(ChunkKey key, JobState state)
    {
        if (disposed) return;
        try
        {
            if (state.Priority) { state.Priority = false; priorityJobs.Add(key); }
            else jobs.Add(key);
        }
        catch (InvalidOperationException)
        {
            if (pending.TryGetValue(key, out var current) && ReferenceEquals(current, state)) pending.Remove(key);
        }
    }

    private void Work()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                ChunkKey key;
                if (!priorityJobs.TryTake(out key) && !jobs.TryTake(out key, 250, stop.Token)) continue;
                JobState? state;
                lock (pendingGate)
                {
                    if (!pending.TryGetValue(key, out state)) continue;
                    if (state.IsRunning) continue;
                    state.IsRunning = true;
                    state.RetryScheduled = false;
                }

                var outcome = RenderQueueOutcome.RetryLater;
                try
                {
                    outcome = render(key);
                }
                catch
                {
                    // RenderRegion logs the contextual exception. Keep the
                    // worker alive and use the same bounded retry path.
                    outcome = RenderQueueOutcome.RetryLater;
                }

                lock (pendingGate)
                {
                    state.IsRunning = false;
                    if (state.RerunRequested && !disposed)
                    {
                        state.RerunRequested = false;
                        state.RetryAttempts = 0;
                        AddJobLocked(key, state);
                    }
                    else if (outcome == RenderQueueOutcome.RetryLater && !disposed)
                    {
                        ScheduleRetryLocked(key, state);
                    }
                    else
                    {
                        pending.Remove(key);
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private void ScheduleRetryLocked(ChunkKey key, JobState state)
    {
        if (state.RetryAttempts >= MaxRetryAttempts)
        {
            pending.Remove(key);
            return;
        }

        var attempt = state.RetryAttempts++;
        var delay = Math.Min(MaxRetryDelayMs, FirstRetryDelayMs * (1 << Math.Min(attempt, 5)));
        state.RetryScheduled = true;
        var generation = ++state.RetryGeneration;
        _ = RetryAfterDelay(key, state, generation, delay);
    }

    private async Task RetryAfterDelay(ChunkKey key, JobState expected, int generation, int delayMs)
    {
        try { await Task.Delay(delayMs, stop.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }

        lock (pendingGate)
        {
            if (disposed || !pending.TryGetValue(key, out var state)
                || !ReferenceEquals(state, expected) || state.RetryGeneration != generation
                || !state.RetryScheduled) return;
            state.RetryScheduled = false;
            AddJobLocked(key, state);
        }
    }

    public int PendingCount { get { lock (pendingGate) return pending.Count; } }

    public void Dispose()
    {
        lock (pendingGate)
        {
            if (disposed) return;
            disposed = true;
            jobs.CompleteAdding();
            priorityJobs.CompleteAdding();
        }
        stop.Cancel();
        try { Task.WaitAll(workers, TimeSpan.FromSeconds(2)); } catch { }
        stop.Dispose();
        jobs.Dispose();
        priorityJobs.Dispose();
    }
}
