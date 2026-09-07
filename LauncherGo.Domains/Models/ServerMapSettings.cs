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
