using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Text.Json;

namespace LauncherGo.Services;

internal sealed class BackgroundHostState
{
    public int ProcessId { get; set; }
    public long ProcessStartTimeUtcTicks { get; set; }
    public string ExecutablePath { get; set; } = "";
    public DateTimeOffset HeartbeatUtc { get; set; }
    public bool IsRunning { get; set; }
    public string Url { get; set; } = "";
    public string ListenAddress { get; set; } = "";
    public int ListenPort { get; set; }
    public string Error { get; set; } = "";
}

internal static class BackgroundHostFiles
{
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    // OS file handles are released after a crash, unlike a named semaphore count.
    internal static FileStream AcquireHost(string directory)
    {
        Directory.CreateDirectory(directory);
        return new FileStream(Path.Combine(directory, "host.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.Read);
    }

    internal static async Task<FileStream> AcquireControlAsync(string directory, CancellationToken token)
    {
        Directory.CreateDirectory(directory);
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(Path.Combine(directory, "control.lock"), FileMode.OpenOrCreate,
                    FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (DateTime.UtcNow < deadline) { await Task.Delay(100, token); }
        }
    }

    internal static T? Read<T>(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return JsonSerializer.Deserialize<T>(stream, JsonOptions);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return default; }
    }

    internal static async Task WriteAsync<T>(string path, T state)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(state, JsonOptions));
            for (var attempt = 0; ; attempt++)
            {
                try { File.Move(temporary, path, true); break; }
                catch (IOException) when (attempt < 4) { await Task.Delay(50); }
            }
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal static Process? ResolveProcess(int? pid, long startTicks, string executablePath)
    {
        if (pid is not > 0 || startTicks <= 0 || string.IsNullOrWhiteSpace(executablePath)) return null;
        Process? process = null;
        try
        {
            process = Process.GetProcessById(pid.Value);
            if (!process.HasExited && process.StartTime.ToUniversalTime().Ticks == startTicks &&
                string.Equals(process.MainModule?.FileName, executablePath, StringComparison.OrdinalIgnoreCase))
                return process;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
        process?.Dispose();
        return null;
    }

    internal static bool IsListening(string address, int port)
    {
        if (!IPAddress.TryParse(address, out var ip)) return false;
        try { return IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(e => e.Port == port && e.Address.Equals(ip)); }
        catch (NetworkInformationException) { return false; }
    }

    internal static bool IsFresh(DateTimeOffset heartbeat) =>
        heartbeat <= DateTimeOffset.UtcNow.AddSeconds(5) && heartbeat >= DateTimeOffset.UtcNow.AddSeconds(-15);
}
