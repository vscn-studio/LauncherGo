using LauncherGo.Services;
using Microsoft.Extensions.Logging;
using Xunit;

namespace LauncherGo.Tests;

public sealed class MapLifecycleLogTests
{
    [Fact]
    public void CompletedOperationLogsStagesTimingsAndSharedCorrelationId()
    {
        var logger = new CaptureLogger();
        var stages = new MapLifecycleLog(logger, "profile", "stop");
        stages.Stage("send-stop-signal");
        stages.Stage("wait-graceful-exit");
        stages.Complete();
        Assert.Equal(5, logger.Entries.Count);
        Assert.All(logger.Entries, item =>
        {
            Assert.Equal("profile", item["ProfileId"]);
            Assert.Equal("stop", item["Operation"]);
        });
        Assert.Single(logger.Entries.Select(item => item["OperationId"]).Distinct());
        Assert.Equal("send-stop-signal", logger.Entries[1]["Stage"]);
        Assert.IsType<long>(logger.Entries[1]["ElapsedMs"]);
        Assert.Equal("wait-graceful-exit", logger.Entries[3]["Stage"]);
        Assert.IsType<long>(logger.Entries[4]["TotalMs"]);
    }

    [Fact]
    public void CancellationLogsUnfinishedStageAndDoesNotReportSuccess()
    {
        var logger = new CaptureLogger();
        var stages = new MapLifecycleLog(logger, "profile", "start");
        stages.Stage("prepare-host/hash-files");
        stages.Fail(new OperationCanceledException());
        Assert.Equal(2, logger.Entries.Count);
        Assert.Equal("prepare-host/hash-files", logger.Entries[1]["Stage"]);
        Assert.Equal(true, logger.Entries[1]["Cancelled"]);
        Assert.IsType<long>(logger.Entries[1]["ElapsedMs"]);
    }

    private sealed class CaptureLogger : ILogger
    {
        internal List<Dictionary<string, object?>> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add(((IEnumerable<KeyValuePair<string, object?>>)state!).ToDictionary(item => item.Key, item => item.Value));
    }
}
