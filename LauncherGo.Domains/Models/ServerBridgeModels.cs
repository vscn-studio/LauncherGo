using System.Text.Json.Nodes;

namespace LauncherGo.Domains.Models;

public sealed class ServerBridgeQueryResult
{
    public bool Success { get; init; }
    public JsonObject? Data { get; init; }
    public string? ErrorCode { get; init; }
    public string? Error { get; init; }
    public string? RequestId { get; init; }
    public string? BridgeVersion { get; init; }
}

public sealed class ServerBridgeSubscriptionOptions
{
    public IReadOnlyCollection<string> Events { get; init; } = [];
    public long Since { get; init; }
    // Relay consumers should start at the current event cursor, not replay chat history.
    // Reconnects within the same subscription still resume from the last event.
    public bool StartFromLatest { get; init; }
    public int MaxQueueSize { get; init; } = 256;
}

public sealed class ServerBridgeEvent
{
    public long Sequence { get; init; }
    public string Event { get; init; } = string.Empty;
    public DateTimeOffset TimestampUtc { get; init; } = DateTimeOffset.UtcNow;
    public JsonObject Data { get; init; } = new();
}

public sealed class ServerBridgeSubscription : IAsyncDisposable
{
    private Func<Task>? _dispose;
    public ServerBridgeSubscription(Func<Task> dispose) => _dispose = dispose;
    public ValueTask DisposeAsync()
    {
        var dispose = Interlocked.Exchange(ref _dispose, null);
        return dispose is null ? ValueTask.CompletedTask : new ValueTask(dispose());
    }
}
