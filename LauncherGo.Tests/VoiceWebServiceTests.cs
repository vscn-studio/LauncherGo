using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using LauncherGo.Domains.Models;
using LauncherGo.Services;
using Xunit;

namespace LauncherGo.Tests;

public sealed class VoiceWebServiceTests
{
    [Fact]
    public async Task WebStartsWithGameOfflineAndStoppingOneProfileLeavesOtherWebRunning()
    {
        var root = NewRoot();
        var service = CreateService(root);
        var first = Profile(root, "first"); var second = Profile(root, "second");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false });
        try
        {
            service.SaveSettings(first, Settings()); service.SaveSettings(second, Settings());
            var firstState = await service.StartAsync(first, deadline.Token);
            var secondState = await service.StartAsync(second, deadline.Token);
            Assert.True(firstState.IsRunning); Assert.True(secondState.IsRunning);
            Assert.NotEqual(firstState.ProcessId, secondState.ProcessId);
            Assert.Contains("<!doctype html>", await client.GetStringAsync(firstState.Url, deadline.Token));
            using var health = JsonDocument.Parse(await client.GetStringAsync(firstState.Url + "health", deadline.Token));
            Assert.Equal("LauncherGo.VoiceHost", health.RootElement.GetProperty("service").GetString());
            using var backend = JsonDocument.Parse(await client.GetStringAsync(firstState.Url + "backend-health", deadline.Token));
            Assert.False(backend.RootElement.GetProperty("connected").GetBoolean());
            using var failedSocket = new ClientWebSocket(); failedSocket.Options.Proxy = null;
            await failedSocket.ConnectAsync(new Uri(firstState.Url.Replace("http:", "ws:") + "voice"), deadline.Token);
            var buffer = new byte[4096];
            var unavailable = await failedSocket.ReceiveAsync(buffer, deadline.Token);
            Assert.Contains("\"type\":\"error\"", Encoding.UTF8.GetString(buffer, 0, unavailable.Count));
            await service.StopAsync(first, deadline.Token);
            Assert.False(service.GetStatus(first).IsRunning);
            Assert.True(service.GetStatus(second).IsRunning);
            Assert.Contains("<!doctype html>", await client.GetStringAsync(secondState.Url, deadline.Token));
            // A newly created launcher service can adopt and stop the detached host by its own identity.
            await CreateService(root).StopAsync(second, deadline.Token);
            Assert.False(service.GetStatus(second).IsRunning);
        }
        finally
        {
            await service.StopAsync(first); await service.StopAsync(second);
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task ProxyForwardsCredentialsAndPcmAndWebStopLeavesBackendListening()
    {
        var root = NewRoot(); var service = CreateService(root); var profile = Profile(root, "proxy");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        var settings = Settings();
        using var backend = new HttpListener();
        backend.Prefixes.Add($"http://127.0.0.1:{settings.BackendPort}/"); backend.Start();
        Task? serverTask = null;
        try
        {
            service.SaveSettings(profile, settings);
            var state = await service.StartAsync(profile, deadline.Token);
            var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            serverTask = Task.Run(async () =>
            {
                var request = await backend.GetContextAsync().WaitAsync(deadline.Token);
                using var socket = (await request.AcceptWebSocketAsync(null)).WebSocket;
                var bytes = new byte[4096];
                var hello = await socket.ReceiveAsync(bytes, deadline.Token);
                Assert.Equal("{\"type\":\"hello\",\"token\":\"test-token\"}", Encoding.UTF8.GetString(bytes, 0, hello.Count));
                await socket.SendAsync(Encoding.UTF8.GetBytes("{\"type\":\"accepted\"}"), WebSocketMessageType.Text, true, deadline.Token);
                var pcm = await socket.ReceiveAsync(bytes, deadline.Token);
                Assert.Equal(WebSocketMessageType.Binary, pcm.MessageType);
                Assert.Equal(new byte[] { 1, 2, 3, 4 }, bytes[..pcm.Count]);
                await socket.SendAsync(bytes.AsMemory(0, pcm.Count), WebSocketMessageType.Binary, true, deadline.Token);
                try { await socket.ReceiveAsync(bytes, deadline.Token); }
                catch (WebSocketException) { }
                disconnected.TrySetResult();
            }, deadline.Token);
            using var browser = new ClientWebSocket(); browser.Options.Proxy = null;
            await browser.ConnectAsync(new Uri(state.Url.Replace("http:", "ws:") + "voice"), deadline.Token);
            await browser.SendAsync(Encoding.UTF8.GetBytes("{\"type\":\"hello\",\"token\":\"test-token\"}"), WebSocketMessageType.Text, true, deadline.Token);
            var buffer = new byte[4096];
            var accepted = await browser.ReceiveAsync(buffer, deadline.Token);
            Assert.Contains("accepted", Encoding.UTF8.GetString(buffer, 0, accepted.Count));
            await browser.SendAsync(new byte[] { 1, 2, 3, 4 }, WebSocketMessageType.Binary, true, deadline.Token);
            var echoed = await browser.ReceiveAsync(buffer, deadline.Token);
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, buffer[..echoed.Count]);
            await service.StopAsync(profile, deadline.Token);
            await disconnected.Task.WaitAsync(deadline.Token);
            await serverTask;
            Assert.True(backend.IsListening);
            Assert.True(BackgroundHostFiles.IsListening("127.0.0.1", settings.BackendPort));
            Assert.False(service.GetStatus(profile).IsRunning);
        }
        finally
        {
            deadline.Cancel(); backend.Stop();
            if (serverTask is not null) try { await serverTask; } catch { }
            await service.StopAsync(profile);
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task StopRefusesStatePointingAtAnUnrelatedProcess()
    {
        var root = NewRoot(); var profile = Profile(root, "unrelated"); var service = CreateService(root);
        var runtime = Path.Combine(root, "runtime", "voice-web", profile.Id); Directory.CreateDirectory(runtime);
        using var process = Process.GetCurrentProcess();
        try
        {
            await BackgroundHostFiles.WriteAsync(Path.Combine(runtime, "host.state.json"), new BackgroundHostState
            {
                ProcessId = process.Id, ProcessStartTimeUtcTicks = process.StartTime.ToUniversalTime().Ticks,
                ExecutablePath = process.MainModule!.FileName, IsRunning = true, HeartbeatUtc = DateTimeOffset.UtcNow
            });
            await service.StopAsync(profile);
            Assert.False(File.Exists(Path.Combine(runtime, "host.stop")));
            Assert.False(process.HasExited);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void SaveSeparatesPortsAndPreservesGameConfiguration()
    {
        var root = NewRoot(); var profile = Profile(root, "config"); var service = CreateService(root);
        var modPath = Path.Combine(profile.DirectoryPath, "ModConfig", "SimpleVoiceChat.Server.json");
        Directory.CreateDirectory(Path.GetDirectoryName(modPath)!);
        File.WriteAllText(modPath, "{\"WebMicrophonePort\":5082,\"MaxChannels\":123}");
        try
        {
            service.SaveSettings(profile, new VoiceSettings { Enabled = false });
            using var json = JsonDocument.Parse(File.ReadAllText(modPath));
            Assert.Equal(15082, json.RootElement.GetProperty("WebMicrophonePort").GetInt32());
            Assert.Equal(123, json.RootElement.GetProperty("MaxChannels").GetInt32());
            Assert.True(json.RootElement.GetProperty("EnableWebMicrophone").GetBoolean());
            Assert.Equal("127.0.0.1", json.RootElement.GetProperty("WebMicrophoneBindAddress").GetString());
            Assert.False(service.LoadSettings(profile).Enabled);
            Assert.True(File.Exists(modPath + ".voice-web.bak"));
            Assert.Throws<InvalidOperationException>(() => service.SaveSettings(profile, new VoiceSettings { BackendPort = 5082 }));
        }
        finally { Directory.Delete(root, true); }
    }

    private static string NewRoot() { var root = Path.Combine(Path.GetTempPath(), "launchergo-voice-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root); return root; }
    private static InstanceProfile Profile(string root, string id) => new() { Id = id, Name = id, DirectoryPath = Path.Combine(root, id) };
    private static VoiceSettings Settings()
    {
        var webPort = FreePort(); int backendPort;
        do { backendPort = FreePort(); } while (backendPort == webPort);
        return new VoiceSettings { ListenPort = webPort, BackendPort = backendPort };
    }
    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
    private static VoiceWebService CreateService(string root)
    {
        var configuration = typeof(VoiceWebServiceTests).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()!.Configuration;
        var host = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "LauncherGo.VoiceHost", "bin", configuration, "net10.0",
            OperatingSystem.IsWindows() ? "LauncherGo.VoiceHost.exe" : "LauncherGo.VoiceHost"));
        Assert.True(File.Exists(host), host);
        return new VoiceWebService(host, Path.Combine(root, "runtime"));
    }
}
