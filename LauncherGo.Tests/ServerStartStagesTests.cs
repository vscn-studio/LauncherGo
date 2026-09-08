using LauncherGo.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LauncherGo.Tests;

public sealed class ServerStartStagesTests
{
    private static ServerStartStages Create(ILogger? logger = null) =>
        new("test-profile", "测试服", logger ?? NullLogger.Instance, _ => { });

    [Fact]
    public async Task EachStageHasIndependentBudgetAndLogsItsDuration()
    {
        var logger = new TestLogger();
        var stages = Create(logger);
        await stages.RunAsync("prepare", TimeSpan.FromSeconds(1), ct => Task.Delay(20, ct), default);
        await stages.RunAsync("control", TimeSpan.FromSeconds(1), ct => Task.Delay(20, ct), default);
        Assert.Equal(2, logger.Entries.Count(entry => entry.Contains("stage completed")));
        Assert.Contains(logger.Entries, entry => entry.Contains("Stage=prepare") && entry.Contains("ElapsedMs="));
        Assert.Contains(logger.Entries, entry => entry.Contains("Stage=control") && entry.Contains("ElapsedMs="));
    }

    [Fact]
    public async Task TimeoutReportsStageAndHoldsGateUntilLateWorkSettles()
    {
        var stages = Create();
        using var gate = new SemaphoreSlim(1);
        var lease = await stages.AcquireGateAsync(gate, default);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var unblock = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resource = new TestResource();
        var task = stages.RunAsync("Host cache", TimeSpan.FromMilliseconds(200), async _ =>
        {
            started.SetResult();
            await unblock.Task; // Simulate Windows IO ignoring cancellation.
            return resource;
        }, default);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var error = await Assert.ThrowsAsync<TimeoutException>(() => task);
        Assert.Contains("Host cache", error.Message);
        Assert.Contains("测试服", error.Message);
        stages.ReleaseWhenSettled(lease.Dispose);
        Assert.False(gate.Wait(0));
        Assert.Equal(0, resource.DisposeCount);
        var retry = gate.WaitAsync();
        Assert.False(retry.IsCompleted);
        unblock.SetResult();
        await stages.PendingCleanup.WaitAsync(TimeSpan.FromSeconds(5));
        await retry.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, resource.DisposeCount);
        gate.Release();
    }

    [Fact]
    public async Task CancellationDuringBlockingPreparationCannotRunNextSideEffect()
    {
        var stages = Create();
        using var cts = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var unblock = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var launches = 0;
        var task = stages.RunAsync("prepare", TimeSpan.FromMinutes(1), async ct =>
        {
            started.SetResult();
            await unblock.Task;
            ct.ThrowIfCancellationRequested();
            Interlocked.Increment(ref launches);
        }, cts.Token);
        await started.Task;
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        unblock.SetResult();
        await stages.PendingCleanup;
        Assert.Equal(0, launches);
    }

    [Fact]
    public async Task LateSuccessfulControlResultIsDiscardedAfterCancellation()
    {
        var stages = Create();
        using var cts = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var result = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var discarded = 0;
        var task = stages.RunAsync("control", TimeSpan.FromMinutes(1), _ =>
        {
            started.SetResult();
            return result.Task;
        }, cts.Token, value => discarded = value);
        await started.Task;
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        result.SetResult(123);
        await stages.PendingCleanup;
        Assert.Equal(123, discarded);
    }

    [Fact]
    public async Task LateFailureIsObservedAndGateCanBeReleased()
    {
        var logger = new TestLogger();
        var stages = Create(logger);
        using var cts = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var result = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var task = stages.RunAsync("prepare", TimeSpan.FromMinutes(1), _ => { started.SetResult(); return result.Task; }, cts.Token);
        await started.Task;
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        result.SetException(new IOException("late IO error"));
        await stages.PendingCleanup;
        Assert.Contains(logger.Entries, entry => entry.Contains("cleanup observed"));
    }

    [Fact]
    public async Task AlreadyCancelledStageDoesNotInvokeOperation()
    {
        var invoked = false;
        var stages = Create();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stages.RunAsync("prepare", TimeSpan.FromSeconds(1), _ =>
        {
            invoked = true;
            return Task.CompletedTask;
        }, new CancellationToken(true)));
        Assert.False(invoked);
    }

    [Fact]
    public async Task NormalStageFailurePreservesOriginalException()
    {
        var stages = Create();
        var expected = new IOException("missing file");
        var actual = await Assert.ThrowsAsync<IOException>(() => stages.RunAsync("prepare", TimeSpan.FromSeconds(1), _ => Task.FromException(expected), default));
        Assert.Same(expected, actual);
        await stages.PendingCleanup;
    }

    [Fact]
    public async Task CancelledGateWaitDoesNotReleaseAnotherOperationsLock()
    {
        var stages = Create();
        using var gate = new SemaphoreSlim(0, 1);
        using var cts = new CancellationTokenSource();
        var task = stages.AcquireGateAsync(gate, cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        await stages.PendingCleanup;
        Assert.Equal(0, gate.CurrentCount);
    }

    private sealed class TestResource : IDisposable
    {
        internal int DisposeCount;
        public void Dispose() => Interlocked.Increment(ref DisposeCount);
    }

    private sealed class TestLogger : ILogger
    {
        internal readonly System.Collections.Concurrent.ConcurrentQueue<string> Entries = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Enqueue(formatter(state, exception));
    }
}
