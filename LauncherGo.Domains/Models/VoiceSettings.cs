namespace LauncherGo.Domains.Models;

public sealed class VoiceSettings
{
    public bool Enabled { get; init; } = true;
    public string ListenAddress { get; init; } = "127.0.0.1";
    public int ListenPort { get; init; } = 5082;
    public bool UseHttps { get; init; }
    public string PublicUrl { get; init; } = string.Empty;
    public int BackendPort { get; init; } = 15082;
    public string CertificatePath { get; init; } = string.Empty;
    public string PrivateKeyPath { get; init; } = string.Empty;
}

public sealed class VoiceRuntimeStatus
{
    public string ProfileId { get; init; } = string.Empty;
    public bool IsRunning { get; init; }
    public int? ProcessId { get; init; }
    public string Url { get; init; } = string.Empty;
    public string Error { get; init; } = string.Empty;
}
