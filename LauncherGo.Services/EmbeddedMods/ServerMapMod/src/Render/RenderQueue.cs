using System.Collections.Concurrent;
using ServerMap.World;

namespace ServerMap.Render;

public enum RenderQueueOutcome
{
    Completed,
    Yield,
    WaitForSignal,
    RetryLater
}

public sealed class RenderQueue : IDisposable
{
    private const int MaxRetryAttempts = 12;
    private const int FirstRetryDelayMs = 1000;
    private const int MaxRetryDelayMs = 30000;

    private sealed class JobState
    {
        public long Version;
        public bool Seasonal;
        public bool IsRunning;
        public bool RerunRequested;
        public bool RetryScheduled;
        public int RetryAttempts;
        public int RetryGeneration;
        public bool Priority;
        public int Ticket;
    }

    private readonly record struct Job(ChunkKey Key, JobState State, int Ticket);
    private readonly BlockingCollection<Job> jobs = new();
    private readonly BlockingCollection<Job> continuationJobs = new();
    private readonly BlockingCollection<Job> seasonalJobs = new();
    private readonly BlockingCollection<Job> priorityJobs = new();
    private readonly Dictionary<ChunkKey, JobState> pending = [];
    private readonly object pendingGate = new();
    private readonly CancellationTokenSource stop = new();
    private readonly Task[] workers;
    private readonly Func<ChunkKey, RenderQueueOutcome> render;
    private bool disposed;
    private Task? shutdown;
    private long completed, failed;
    private DateTimeOffset? lastCompletedAt;

    public RenderQueue(int threadCount, Func<ChunkKey, RenderQueueOutcome> render)
    {
        this.render = render;
        workers = Enumerable.Range(0, Math.Clamp(threadCount, 1, 4)).Select(_ => Task.Run(Work)).ToArray();
    }

    public void Enqueue(ChunkKey key, bool priority = false, long version = 0, bool seasonal = false)
    {
        lock (pendingGate)
        {
            if (disposed) return;
            if (pending.TryGetValue(key, out var current))
            {
                var promoteBackground = current.Seasonal && !seasonal;
                current.Seasonal &= seasonal;
                if (current.IsRunning)
                {
                    // A save while rendering must result in one fresh pass.
                    current.RerunRequested |= version == 0 || version > current.Version;
                    current.Version = Math.Max(current.Version, version);
                    current.Priority |= priority;
                    return;
                }

                current.Version = Math.Max(current.Version, version);
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
                else if (priority && !current.Priority || promoteBackground)
                {
                    current.Priority |= priority;
                    AddJobLocked(key, current);
                }
                // Otherwise the key is already waiting in BlockingCollection.
                return;
            }

            var state = new JobState { Priority = priority, Version = version, Seasonal = seasonal };
            pending[key] = state;
            AddJobLocked(key, state);
        }
    }

    private void AddJobLocked(ChunkKey key, JobState state, bool continuation = false)
    {
        if (disposed) return;
        try
        {
            var job = new Job(key, state, ++state.Ticket);
            if (state.Priority) priorityJobs.Add(job);
            else if (state.Seasonal) seasonalJobs.Add(job);
            else if (continuation) continuationJobs.Add(job);
            else jobs.Add(job);
        }
        catch (InvalidOperationException)
        {
            if (pending.TryGetValue(key, out var current) && ReferenceEquals(current, state)) pending.Remove(key);
        }
    }

    /// <summary>Promote a queued background job without enqueuing a duplicate.</summary>
    public bool Promote(ChunkKey key)
    {
        lock (pendingGate)
        {
            if (disposed || !pending.TryGetValue(key, out var state)) return false;
            if (state.IsRunning || state.RetryScheduled) return true;
            if (state.Priority) return true;
            state.Priority = true;
            AddJobLocked(key, state);
            return true;
        }
    }

    private void Work()
    {
        try
        {
            var highPriorityUnits = 0;
            while (!stop.IsCancellationRequested)
            {
                Job job;
                if (highPriorityUnits >= 8 && (continuationJobs.TryTake(out job) || jobs.TryTake(out job) || seasonalJobs.TryTake(out job))) highPriorityUnits = 0;
                else if (priorityJobs.TryTake(out job)) highPriorityUnits++;
                else if (continuationJobs.TryTake(out job) || jobs.TryTake(out job) || seasonalJobs.TryTake(out job)) highPriorityUnits = 0;
                else if (priorityJobs.TryTake(out job, 50, stop.Token)) highPriorityUnits++;
                else continue;
                var key = job.Key;
                JobState? state;
                lock (pendingGate)
                {
                    if (!pending.TryGetValue(key, out state)) continue;
                    if (!ReferenceEquals(state, job.State) || state.Ticket != job.Ticket || state.IsRunning) continue;
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
                    else if (outcome == RenderQueueOutcome.Yield && !disposed)
                    {
                        state.RetryAttempts = 0;
                        AddJobLocked(key, state, continuation: true);
                    }
                    else if (outcome == RenderQueueOutcome.RetryLater && !disposed)
                    {
                        ScheduleRetryLocked(key, state);
                    }
                    else
                    {
                        pending.Remove(key);
                        if (outcome == RenderQueueOutcome.Completed) { completed++; lastCompletedAt = DateTimeOffset.UtcNow; }
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
            failed++;
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
    public sealed record QueueProgress(int Queued, int Active, int Retrying, long Completed, long Failed, DateTimeOffset? LastCompletedAt);
    public QueueProgress Progress
    {
        get { lock (pendingGate) return new(pending.Values.Count(v => !v.IsRunning && !v.RetryScheduled), pending.Values.Count(v => v.IsRunning), pending.Values.Count(v => v.RetryScheduled), completed, failed, lastCompletedAt); }
    }

    public Task StopAsync()
    {
        lock (pendingGate)
        {
            if (shutdown != null) return shutdown;
            disposed = true;
            jobs.CompleteAdding(); continuationJobs.CompleteAdding(); seasonalJobs.CompleteAdding();
            priorityJobs.CompleteAdding();
            stop.Cancel();
            shutdown = FinishStopAsync();
            return shutdown;
        }
    }

    private async Task FinishStopAsync()
    {
        await Task.WhenAll(workers).ConfigureAwait(false);
        jobs.Dispose(); continuationJobs.Dispose(); seasonalJobs.Dispose(); priorityJobs.Dispose();
        // Delayed retry continuations may still observe stop.Token.
    }

    public void Dispose() => _ = StopAsync();
}
