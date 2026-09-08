using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace LauncherGo.Services;

// Each stage has its own budget; a slow cold-cache preparation must not consume
// the control-channel or automation budget. No process arguments/tokens are logged.
internal sealed class ServerStartStages(string profileId, string profileName, ILogger logger, Action<string> output)
{
    internal static readonly TimeSpan PreparationTimeout = TimeSpan.FromMinutes(2);
    internal static readonly TimeSpan ControlTimeout = TimeSpan.FromSeconds(60);
    internal static readonly TimeSpan StandardTimeout = TimeSpan.FromSeconds(60);
    internal static readonly TimeSpan AutomationTimeout = TimeSpan.FromMinutes(10);
    internal Task PendingCleanup { get; private set; } = Task.CompletedTask;

    internal Task<IDisposable> AcquireGateAsync(SemaphoreSlim gate, CancellationToken token) =>
        RunAsync<IDisposable>("等待启动锁 / Waiting for previous operation", StandardTimeout, async ct =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            return new GateLease(gate);
        }, token);

    private sealed class GateLease(SemaphoreSlim gate) : IDisposable
    {
        private int released;
        public void Dispose() { if (Interlocked.Exchange(ref released, 1) == 0) gate.Release(); }
    }

    internal async Task<T> RunAsync<T>(string stage, TimeSpan timeout, Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken, Action<T>? discardResult = null)
    {
        if (!PendingCleanup.IsCompleted)
            throw new InvalidOperationException("上一个启动阶段仍在取消清理中。 / Previous startup stage is still settling.");
        cancellationToken.ThrowIfCancellationRequested();
        var clock = Stopwatch.StartNew();
        var stageCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        stageCts.CancelAfter(timeout);
        logger.LogInformation("Server startup stage started. ProfileId={ProfileId}, Stage={Stage}, TimeoutSeconds={TimeoutSeconds}.",
            profileId, stage, timeout.TotalSeconds);
        output($"[system] 启动阶段 / Startup stage: {stage}（限时 / limit {timeout.TotalSeconds:0} s）");
        // Isolate synchronous filesystem/OS calls so the caller can observe the deadline.
        // The operation must check its token before each subsequent side effect.
        var task = Task.Run(() => operation(stageCts.Token), CancellationToken.None);
        try
        {
            var result = await task.WaitAsync(stageCts.Token).ConfigureAwait(false);
            stageCts.Token.ThrowIfCancellationRequested();
            logger.LogInformation("Server startup stage completed. ProfileId={ProfileId}, Stage={Stage}, ElapsedMs={ElapsedMs}.",
                profileId, stage, clock.ElapsedMilliseconds);
            output($"[system] 启动阶段完成 / Startup stage completed: {stage}（{clock.Elapsed.TotalSeconds:F2} s）");
            stageCts.Dispose();
            return result;
        }
        catch (Exception error)
        {
            var timedOut = stageCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested;
            // Observe late errors and dispose late results (notably prepared Host leases).
            // Keep the CTS alive until all synchronous work actually returns.
            PendingCleanup = SettleAsync(task, stageCts, stage, clock, discardResult);
            logger.LogWarning(error, "Server startup stage failed. ProfileId={ProfileId}, Stage={Stage}, ElapsedMs={ElapsedMs}, TimedOut={TimedOut}.",
                profileId, stage, clock.ElapsedMilliseconds, timedOut);
            if (timedOut)
                throw new TimeoutException($"启动服务器阶段超时：{profileName}；阶段：{stage}；已耗时 {clock.Elapsed.TotalSeconds:F1} 秒，限时 {timeout.TotalSeconds:0} 秒。后台操作若尚未返回，将等待其安全结束后才能重试。 / Server startup stage timed out: {profileName}; stage: {stage.Split(" / ")[^1]}; elapsed: {clock.Elapsed.TotalSeconds:F1} s; limit: {timeout.TotalSeconds:0} s. Pending work must settle before retrying.");
            throw;
        }
    }

    internal Task RunAsync(string stage, TimeSpan timeout, Func<CancellationToken, Task> operation, CancellationToken token) =>
        RunAsync(stage, timeout, async ct => { await operation(ct).ConfigureAwait(false); return 0; }, token);

    private async Task SettleAsync<T>(Task<T> task, CancellationTokenSource cts, string stage, Stopwatch clock, Action<T>? discardResult)
    {
        try
        {
            var result = await task.ConfigureAwait(false);
            if (discardResult is not null) discardResult(result);
            else if (result is IDisposable resource) resource.Dispose();
        }
        catch (Exception error) { logger.LogDebug(error, "Startup stage cleanup observed. ProfileId={ProfileId}, Stage={Stage}.", profileId, stage); }
        finally
        {
            cts.Dispose();
            logger.LogInformation("Server startup stage settled. ProfileId={ProfileId}, Stage={Stage}, ElapsedMs={ElapsedMs}.", profileId, stage, clock.ElapsedMilliseconds);
        }
    }

    internal void ReleaseWhenSettled(Action release)
    {
        if (PendingCleanup.IsCompleted) release();
        else _ = ReleaseAsync();
        async Task ReleaseAsync()
        {
            try { await PendingCleanup.ConfigureAwait(false); }
            finally { release(); }
        }
    }
}
