using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using LauncherGo.Domains.Models;
using LauncherGo.Services;
using Xunit;
using Xunit.Abstractions;

namespace LauncherGo.Tests;

public sealed class ServerMapHostShutdownTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Stop_ExitsGracefullyWithSseAndPendingBackendRequest(bool activeRequests)
    {
        // Isolated temporary Host and fake backend; never touches installed profiles/services.
        var root = Path.Combine(Path.GetTempPath(), $"launchergo-map-stop-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var token = deadline.Token;
        using var backend = new TcpListener(IPAddress.Loopback, 0);
        backend.Start();
        var backendPort = ((IPEndPoint)backend.LocalEndpoint).Port;
        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var hostPort = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        var config = Path.Combine(root, "host.json");
        var state = Path.Combine(root, "host.state.json");
        var stop = Path.Combine(root, "host.stop");
        await File.WriteAllTextAsync(config, JsonSerializer.Serialize(new ServerMapSettings
        {
            ListenAddress = "127.0.0.1", ListenPort = hostPort, BackendPort = backendPort,
            WebRoot = root
        }), token);
        var configuration = typeof(ServerMapHostShutdownTests).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()!.Configuration;
        var dll = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..",
            "LauncherGo.ServerMapHost", "bin", configuration, "net10.0", "LauncherGo.ServerMapHost.dll"));
        Assert.True(File.Exists(dll), dll);
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
            RedirectStandardError = true, WorkingDirectory = root
        };
        foreach (var arg in new[] { dll, "--config", config, "--state", state, "--stop", stop }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = Timeout.InfiniteTimeSpan };
        HttpResponseMessage? streamResponse = null;
        Task? streamingBackend = null;
        Task? pendingBackend = null;
        Task<HttpResponseMessage>? pendingResponse = null;
        try
        {
            while (BackgroundHostFiles.Read<BackgroundHostState>(state)?.IsRunning != true)
            {
                Assert.False(process.HasExited, "Temporary map Host exited before becoming ready.");
                await Task.Delay(50, token);
            }
            if (activeRequests)
            {
                var streamingStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                streamingBackend = ServeBackendAsync(backend, true, streamingStarted, token);
                streamResponse = await client.GetAsync($"http://127.0.0.1:{hostPort}/api/v1/events", HttpCompletionOption.ResponseHeadersRead, token);
                await streamingStarted.Task.WaitAsync(token);
                streamResponse.EnsureSuccessStatusCode();
                var pendingStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                pendingBackend = ServeBackendAsync(backend, false, pendingStarted, token);
                pendingResponse = client.GetAsync($"http://127.0.0.1:{hostPort}/api/hang", HttpCompletionOption.ResponseHeadersRead, token);
                await pendingStarted.Task.WaitAsync(token);
            }
            var clock = Stopwatch.StartNew();
            await File.WriteAllTextAsync(stop, "stop", token);
            await process.WaitForExitAsync(token).WaitAsync(TimeSpan.FromSeconds(4), token);
            output.WriteLine($"ActiveRequests={activeRequests}; graceful exit in {clock.ElapsedMilliseconds} ms.");
            Assert.Equal(0, process.ExitCode); // No parent-side force kill.
            Assert.False(BackgroundHostFiles.Read<BackgroundHostState>(state)!.IsRunning);
            if (streamingBackend is not null) await streamingBackend.WaitAsync(token);
            if (pendingBackend is not null) await pendingBackend.WaitAsync(token);
            if (pendingResponse is not null)
                Assert.NotNull(await Record.ExceptionAsync(async () => { using var response = await pendingResponse.WaitAsync(token); }));
            Assert.Contains("cancel-active-requests", await stdout);
            Assert.Contains("stop-listener", await stdout);
        }
        finally
        {
            deadline.Cancel();
            streamResponse?.Dispose();
            if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); }
            foreach (var task in new Task?[] { streamingBackend, pendingBackend, pendingResponse })
                if (task is not null) try { await task; } catch (Exception) { }
            output.WriteLine(await stdout);
            output.WriteLine(await stderr);
            Directory.Delete(root, true);
        }
    }

    private static async Task ServeBackendAsync(TcpListener listener, bool sse, TaskCompletionSource started, CancellationToken token)
    {
        using var connection = await listener.AcceptTcpClientAsync(token);
        await using var stream = connection.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
        while (await reader.ReadLineAsync(token) is { Length: > 0 }) { }
        if (sse)
        {
            // Leave the body open indefinitely, exactly like an EventSource connection.
            var data = "HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nConnection: close\r\n\r\ndata: " + new string('x', 8192) + "\n\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(data), token);
            await stream.FlushAsync(token);
        }
        started.SetResult();
        try
        {
            var buffer = new byte[1];
            while (await stream.ReadAsync(buffer, token) != 0) { }
        }
        catch (IOException) { } // TCP reset also confirms upstream cancellation.
    }
}
