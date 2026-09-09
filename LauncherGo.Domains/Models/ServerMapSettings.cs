namespace LauncherGo.Domains.Models;

public sealed class ServerMapSettings
{
    public bool Enabled { get; init; } = true;
    public string ListenAddress { get; init; } = "127.0.0.1";
    public int ListenPort { get; init; } = 5081;
    public bool UseHttps { get; init; }
    public string CertificatePath { get; init; } = string.Empty;
    public string PrivateKeyPath { get; init; } = string.Empty;
    public string WebRoot { get; init; } = string.Empty;
    public int BackendPort { get; init; } = 5080;
    public string BackendToken { get; init; } = string.Empty;
    public string PublicUrl { get; init; } = string.Empty;
}

public sealed class ServerMapRuntimeStatus
{
    public string ProfileId { get; init; } = string.Empty;
    public bool IsRunning { get; init; }
    public int ProcessId { get; init; }
    public string Error { get; init; } = string.Empty;
    public string Url { get; init; } = string.Empty;
}

public sealed class ServerMapRenderProgress
{
    public int CacheProtocol { get; init; }
    public string Phase { get; init; } = "";
    public string Reason { get; init; } = "";
    public bool Rebuilding { get; init; }
    public string RebuildId { get; init; } = "";
    public long Pending { get; init; }
    public long Completed { get; init; }
    public long Failed { get; init; }
    public long SurfaceExtraction { get; init; }
    public long Coloring { get; init; }
    public long Parents { get; init; }
    public long Indexing { get; init; }
    public long AwaitingSave { get; init; }
    public long DeferredGeneration { get; init; }
    public long TranslocatorCount { get; init; }
    public string? Error { get; init; }
}
