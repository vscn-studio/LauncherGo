namespace LauncherGo.Domains.Models;

/// <summary>
///     TCP 网关的运行时状态，由 GatewayHost 写入并由启动器读取。
/// </summary>
public sealed class TcpGatewayRuntimeStatus
{
    public bool IsRunning { get; set; }

    public bool IsListening { get; set; }

    /// <summary>
    ///     监听地址或端口已变更，需重启网关才会生效。
    /// </summary>
    public bool RequiresRestart { get; set; }

    public string PendingRestartReason { get; set; } = string.Empty;

    public int? ProcessId { get; set; }
    public long ProcessStartTimeUtcTicks { get; set; }
    public string ExecutablePath { get; set; } = "";
    public DateTimeOffset HeartbeatUtc { get; set; }

    public DateTimeOffset? StartedAtUtc { get; set; }

    public string ListenAddress { get; set; } = string.Empty;

    public int ActiveConnections { get; set; }

    public long AcceptedConnections { get; set; }

    public long RejectedConnections { get; set; }

    public long FailedConnections { get; set; }

    public long ClientToBackendBytes { get; set; }

    public long BackendToClientBytes { get; set; }

    public string LastError { get; set; } = string.Empty;

    public string RoutingHistoryLogPath { get; set; } = string.Empty;

    public List<TcpGatewayRoutingHistoryEntry> RoutingHistory { get; set; } = [];

    public List<TcpGatewayBackendRuntimeStatus> Backends { get; set; } = [];
}

public sealed class TcpGatewayBackendRuntimeStatus
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Address { get; set; } = string.Empty;

    public bool Enabled { get; set; }

    public TcpGatewayBackendRoutingState RoutingState { get; set; }

    public int Weight { get; set; }

    public string ProfileId { get; set; } = string.Empty;

    public bool IsHealthy { get; set; }

    public int ActiveConnections { get; set; }

    public string LastError { get; set; } = string.Empty;

    public TcpGatewayBackendStatistics Statistics { get; set; } = new();

}

/// <summary>
///     单个网关后端自开始统计以来的转发数据。
/// </summary>
public sealed class TcpGatewayBackendStatistics
{
    public DateTimeOffset StartedAtUtc { get; set; }

    public long ClientToBackendBytes { get; set; }

    public long BackendToClientBytes { get; set; }

    public double CurrentClientToBackendMbps { get; set; }

    public double CurrentBackendToClientMbps { get; set; }

    public double PeakClientToBackendMbps { get; set; }

    public double PeakBackendToClientMbps { get; set; }

    public double AverageClientToBackendMbps { get; set; }

    public double AverageBackendToClientMbps { get; set; }

    public int CurrentConnections { get; set; }

    public int PeakConnections { get; set; }

    public long EstablishedConnections { get; set; }

    public long FailedConnections { get; set; }

    public double ConnectionEstablishRatePerMinute { get; set; }

    public double ConnectionFailureRate { get; set; }

    public double AverageBackendConnectLatencyMilliseconds { get; set; }

    public string LastDisconnectReason { get; set; } = string.Empty;

    public DateTimeOffset? LastDisconnectAtUtc { get; set; }

    public List<TcpGatewayDisconnectRecord> RecentDisconnects { get; set; } = [];
}

public sealed class TcpGatewayDisconnectRecord
{
    public DateTimeOffset OccurredAtUtc { get; set; }

    public string Type { get; set; } = string.Empty;

    public string Details { get; set; } = string.Empty;
}
