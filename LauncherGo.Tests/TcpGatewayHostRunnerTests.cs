using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using LauncherGo.Domains.Models;
using LauncherGo.Services;
using Xunit;

namespace LauncherGo.Tests;

public sealed class TcpGatewayHostRunnerTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task RunAsync_RelaysTcpTrafficAndStopsFromSignal()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), "LauncherGo.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testRoot);

        using var backendListener = new TcpListener(IPAddress.Loopback, 0);
        backendListener.Start();
        var backendPort = ((IPEndPoint)backendListener.LocalEndpoint).Port;
        var gatewayPort = ReserveTcpPort();
        using var backendCts = new CancellationTokenSource();
        var backendTask = RunEchoServerAsync(backendListener, backendCts.Token);

        var configPath = Path.Combine(testRoot, "gateway-config.json");
        var statePath = Path.Combine(testRoot, "gateway-state.json");
        var stopSignalPath = Path.Combine(testRoot, "gateway.stop");
        var reloadSignalPath = Path.Combine(testRoot, "gateway.reload");
        var settings = new TcpGatewaySettings
        {
            ListenHost = "127.0.0.1",
            ListenPort = gatewayPort,
            MaxConnections = 8,
            MaxConnectionsPerIp = 4,
            ConnectTimeoutSec = 2,
            HealthCheckIntervalSec = 1,
            Backends =
            [
                new TcpGatewayBackend
                {
                    Id = "echo",
                    Name = "Echo",
                    Host = "127.0.0.1",
                    Port = backendPort,
                    Enabled = true
                }
            ]
        };

        Task<int>? hostTask = null;
        try
        {
            await File.WriteAllTextAsync(configPath, JsonSerializer.Serialize(settings));
            hostTask = TcpGatewayHostRunner.RunAsync(
                [
                    "--config", configPath,
                    "--state", statePath,
                    "--stop-signal", stopSignalPath,
                    "--reload-signal", reloadSignalPath
                ]);

            var started = await WaitForStartedAsync(statePath, hostTask);
            Assert.True(started.IsRunning);

            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, gatewayPort);
            var stream = client.GetStream();
            var payload = "gateway-relay"u8.ToArray();
            await stream.WriteAsync(payload);
            var received = new byte[payload.Length];
            var offset = 0;
            while (offset < received.Length)
            {
                var read = await stream.ReadAsync(received.AsMemory(offset));
                Assert.True(read > 0);
                offset += read;
            }

            Assert.Equal(payload, received);

            await File.WriteAllTextAsync(stopSignalPath, "stop");
            Assert.Equal(0, await hostTask.WaitAsync(TimeSpan.FromSeconds(5)));
            var stopped = JsonSerializer.Deserialize<TcpGatewayRuntimeStatus>(await File.ReadAllTextAsync(statePath), JsonOptions);
            Assert.NotNull(stopped);
            Assert.False(stopped.IsRunning);
            Assert.False(stopped.IsListening);
            var backendStatus = Assert.Single(stopped.Backends);
            Assert.True(backendStatus.Statistics.ClientToBackendBytes >= payload.Length);
            Assert.True(backendStatus.Statistics.BackendToClientBytes >= payload.Length);
            Assert.True(backendStatus.Statistics.CurrentClientToBackendMbps > 0);
            Assert.True(backendStatus.Statistics.CurrentBackendToClientMbps > 0);
            Assert.True(backendStatus.Statistics.PeakConnections >= 1);
            Assert.True(backendStatus.Statistics.EstablishedConnections >= 1);
            Assert.Contains(backendStatus.Statistics.RecentDisconnects, record => record.Type == "GatewayStopped");
            Assert.False(Directory.Exists(Path.Combine(testRoot, "statistics")));
        }
        finally
        {
            await File.WriteAllTextAsync(stopSignalPath, "stop");
            if (hostTask is not null)
            {
                await IgnoreCancellationAsync(hostTask);
            }
            backendCts.Cancel();
            backendListener.Stop();
            await IgnoreCancellationAsync(backendTask);
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public void ValidateSettings_RejectsInvalidIpRules()
    {
        var settings = new TcpGatewaySettings
        {
            Backends =
            [
                new TcpGatewayBackend
                {
                    Id = "valid",
                    Host = "127.0.0.1",
                    Port = 42420
                }
            ],
            AllowListText = "not-an-ip"
        };

        Assert.Throws<InvalidOperationException>(() => TcpGatewayHostRunner.ValidateSettings(settings));
    }

    [Fact]
    public async Task RunAsync_RoutesOneTimeTransferTicketToDrainingBackendOnMainListener()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), "LauncherGo.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testRoot);

        using var onlineListener = new TcpListener(IPAddress.Loopback, 0);
        using var drainingListener = new TcpListener(IPAddress.Loopback, 0);
        onlineListener.Start();
        drainingListener.Start();
        var onlinePort = ((IPEndPoint)onlineListener.LocalEndpoint).Port;
        var drainingPort = ((IPEndPoint)drainingListener.LocalEndpoint).Port;
        var gatewayPort = ReserveTcpPort();
        const string ticketSecret = "gateway-ticket-test-secret";
        using var backendCts = new CancellationTokenSource();
        var onlineTask = RunResponseServerAsync(onlineListener, (byte)'O', backendCts.Token);
        var drainingTask = RunResponseServerAsync(drainingListener, (byte)'D', backendCts.Token);

        var configPath = Path.Combine(testRoot, "gateway-config.json");
        var statePath = Path.Combine(testRoot, "gateway-state.json");
        var stopSignalPath = Path.Combine(testRoot, "gateway.stop");
        var reloadSignalPath = Path.Combine(testRoot, "gateway.reload");
        var settings = new TcpGatewaySettings
        {
            ListenHost = "127.0.0.1",
            ListenPort = gatewayPort,
            MaxConnections = 8,
            MaxConnectionsPerIp = 4,
            ConnectTimeoutSec = 2,
            HealthCheckIntervalSec = 1,
            RedirectTicketSecret = ticketSecret,
            Backends =
            [
                new TcpGatewayBackend
                {
                    Id = "online",
                    Name = "Online",
                    Host = "127.0.0.1",
                    Port = onlinePort,
                    Weight = 100,
                    RoutingState = TcpGatewayBackendRoutingState.Online
                },
                new TcpGatewayBackend
                {
                    Id = "draining",
                    Name = "Draining",
                    Host = "127.0.0.1",
                    Port = drainingPort,
                    Weight = 100,
                    RoutingState = TcpGatewayBackendRoutingState.Draining
                }
            ]
        };

        Task<int>? hostTask = null;
        try
        {
            await File.WriteAllTextAsync(configPath, JsonSerializer.Serialize(settings));
            hostTask = TcpGatewayHostRunner.RunAsync(
                [
                    "--config", configPath,
                    "--state", statePath,
                    "--stop-signal", stopSignalPath,
                    "--reload-signal", reloadSignalPath
                ]);

            await WaitForStartedAsync(statePath, hostTask);
            Assert.Equal((byte)'O', await GetGatewayResponseAsync(gatewayPort));
            var ticket = GatewayTransferProtocol.CreateTicket(
                ticketSecret,
                "online",
                "draining",
                "player-uid",
                TimeSpan.FromMinutes(1));
            Assert.Equal((byte)'D', await GetGatewayResponseWithTicketAsync(gatewayPort, ticket));
            await AssertTicketIsRejectedAfterUseAsync(gatewayPort, ticket);

            var state = JsonSerializer.Deserialize<TcpGatewayRuntimeStatus>(await File.ReadAllTextAsync(statePath), JsonOptions);
            Assert.NotNull(state);
            Assert.Equal(TcpGatewayBackendRoutingState.Online, state.Backends.Single(backend => backend.Id == "online").RoutingState);
            Assert.Equal(TcpGatewayBackendRoutingState.Draining, state.Backends.Single(backend => backend.Id == "draining").RoutingState);
        }
        finally
        {
            await File.WriteAllTextAsync(stopSignalPath, "stop");
            if (hostTask is not null) await IgnoreCancellationAsync(hostTask);
            backendCts.Cancel();
            onlineListener.Stop();
            drainingListener.Stop();
            await IgnoreCancellationAsync(onlineTask);
            await IgnoreCancellationAsync(drainingTask);
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_ReloadsBackendsWithoutRestartingTheGateway()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), "LauncherGo.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testRoot);

        using var backendListener = new TcpListener(IPAddress.Loopback, 0);
        backendListener.Start();
        var backendPort = ((IPEndPoint)backendListener.LocalEndpoint).Port;
        var gatewayPort = ReserveTcpPort();
        using var backendCts = new CancellationTokenSource();
        var backendTask = RunEchoServerAsync(backendListener, backendCts.Token);

        var configPath = Path.Combine(testRoot, "gateway-config.json");
        var statePath = Path.Combine(testRoot, "gateway-state.json");
        var stopSignalPath = Path.Combine(testRoot, "gateway.stop");
        var reloadSignalPath = Path.Combine(testRoot, "gateway.reload");
        var settings = new TcpGatewaySettings
        {
            ListenHost = "127.0.0.1",
            ListenPort = gatewayPort,
            MaxConnections = 8,
            MaxConnectionsPerIp = 4,
            ConnectTimeoutSec = 2,
            HealthCheckIntervalSec = 1,
            Backends =
            [
                new TcpGatewayBackend
                {
                    Id = "echo",
                    Name = "Echo",
                    Host = "127.0.0.1",
                    Port = backendPort,
                    Enabled = true
                }
            ]
        };

        Task<int>? hostTask = null;
        try
        {
            await File.WriteAllTextAsync(configPath, JsonSerializer.Serialize(settings));
            hostTask = TcpGatewayHostRunner.RunAsync(
                [
                    "--config", configPath,
                    "--state", statePath,
                    "--stop-signal", stopSignalPath,
                    "--reload-signal", reloadSignalPath
                ]);

            var started = await WaitForStartedAsync(statePath, hostTask);
            var reloadedSettings = new TcpGatewaySettings
            {
                ListenHost = settings.ListenHost,
                ListenPort = settings.ListenPort,
                MaxConnections = settings.MaxConnections,
                MaxConnectionsPerIp = settings.MaxConnectionsPerIp,
                ConnectTimeoutSec = settings.ConnectTimeoutSec,
                HealthCheckIntervalSec = settings.HealthCheckIntervalSec,
                Backends =
                [
                    new TcpGatewayBackend
                    {
                        Id = "echo-reloaded",
                        Name = "Echo reloaded",
                        Host = "127.0.0.1",
                        Port = backendPort,
                        Enabled = true
                    }
                ]
            };

            await WriteSettingsAtomicallyAsync(configPath, reloadedSettings);
            await File.WriteAllTextAsync(reloadSignalPath, Guid.NewGuid().ToString("N"));
            var reloaded = await WaitForStateAsync(
                statePath,
                hostTask,
                status => status.Backends.SingleOrDefault(backend => backend.Id == "echo-reloaded") is not null);

            Assert.Equal(started.ProcessId, reloaded.ProcessId);
            Assert.False(reloaded.RequiresRestart);
            await AssertEchoAsync(gatewayPort, "hot-reload");

            reloadedSettings.ListenPort = ReserveTcpPort();
            await WriteSettingsAtomicallyAsync(configPath, reloadedSettings);
            await File.WriteAllTextAsync(reloadSignalPath, Guid.NewGuid().ToString("N"));
            var restartRequired = await WaitForStateAsync(
                statePath,
                hostTask,
                status => status.RequiresRestart);

            Assert.Equal(started.ProcessId, restartRequired.ProcessId);
            Assert.Equal($"127.0.0.1:{gatewayPort}", restartRequired.ListenAddress);
        }
        finally
        {
            await File.WriteAllTextAsync(stopSignalPath, "stop");
            if (hostTask is not null)
            {
                await IgnoreCancellationAsync(hostTask);
            }

            backendCts.Cancel();
            backendListener.Stop();
            await IgnoreCancellationAsync(backendTask);
            Directory.Delete(testRoot, recursive: true);
        }
    }

    private static int ReserveTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static async Task RunEchoServerAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(cancellationToken);
                _ = EchoClientAsync(client, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Expected during cleanup.
        }
        catch (SocketException) when (cancellationToken.IsCancellationRequested)
        {
            // Stopping the listener ends AcceptTcpClientAsync on some platforms.
        }
    }

    private static async Task RunResponseServerAsync(TcpListener listener, byte response, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(cancellationToken);
                _ = Task.Run(async () =>
                {
                    using (client)
                    {
                        var stream = client.GetStream();
                        var buffer = new byte[32];
                        var read = await stream.ReadAsync(buffer, cancellationToken);
                        if (read > 0)
                        {
                            await stream.WriteAsync(new[] { response }, cancellationToken);
                        }
                    }
                }, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Expected during cleanup.
        }
        catch (SocketException) when (cancellationToken.IsCancellationRequested)
        {
            // Stopping the listener ends AcceptTcpClientAsync on some platforms.
        }
    }

    private static async Task<byte> GetGatewayResponseAsync(int port)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        var stream = client.GetStream();
        await stream.WriteAsync(new[] { (byte)'x' });
        var response = new byte[1];
        var read = await stream.ReadAsync(response);
        Assert.Equal(1, read);
        return response[0];
    }

    private static async Task<byte> GetGatewayResponseWithTicketAsync(int port, string ticket)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        var stream = client.GetStream();
        await GatewayTransferProtocol.WritePreambleAsync(stream, ticket);
        await stream.WriteAsync(new[] { (byte)'x' });
        var response = new byte[1];
        var read = await stream.ReadAsync(response);
        Assert.Equal(1, read);
        return response[0];
    }

    private static async Task AssertTicketIsRejectedAfterUseAsync(int port, string ticket)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        var stream = client.GetStream();
        await GatewayTransferProtocol.WritePreambleAsync(stream, ticket);
        var response = new byte[1];
        var read = await stream.ReadAsync(response);
        Assert.Equal(0, read);
    }

    private static async Task EchoClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            var stream = client.GetStream();
            var buffer = new byte[4096];
            try
            {
                while (true)
                {
                    var read = await stream.ReadAsync(buffer, cancellationToken);
                    if (read == 0)
                    {
                        return;
                    }

                    await stream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Expected during cleanup.
            }
        }
    }

    private static async Task<TcpGatewayRuntimeStatus> WaitForStartedAsync(
        string statePath,
        Task<int> hostTask)
    {
        return await WaitForStateAsync(statePath, hostTask, static state => state.IsListening);
    }

    private static async Task<TcpGatewayRuntimeStatus> WaitForStateAsync(
        string statePath,
        Task<int> hostTask,
        Func<TcpGatewayRuntimeStatus, bool> predicate)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (File.Exists(statePath))
            {
                try
                {
                    var state = JsonSerializer.Deserialize<TcpGatewayRuntimeStatus>(await File.ReadAllTextAsync(statePath), JsonOptions);
                    if (state is not null && predicate(state))
                    {
                        return state;
                    }
                }
                catch (JsonException)
                {
                    // The host replaces state snapshots atomically, but retain a retry for networked filesystems.
                }
            }

            if (hostTask.IsCompleted)
            {
                Assert.Equal(0, await hostTask);
            }

            await Task.Delay(50);
        }

        throw new TimeoutException("Timed out waiting for the TCP gateway state.");
    }

    private static async Task WriteSettingsAtomicallyAsync(string path, TcpGatewaySettings settings)
    {
        var temporaryPath = Path.Combine(
            Path.GetDirectoryName(path)!,
            $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(temporaryPath, JsonSerializer.Serialize(settings));
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static async Task AssertEchoAsync(int gatewayPort, string content)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, gatewayPort);
        var payload = System.Text.Encoding.UTF8.GetBytes(content);
        await client.GetStream().WriteAsync(payload);
        var received = new byte[payload.Length];
        var offset = 0;
        while (offset < received.Length)
        {
            var read = await client.GetStream().ReadAsync(received.AsMemory(offset));
            Assert.True(read > 0);
            offset += read;
        }

        Assert.Equal(payload, received);
    }

    private static async Task IgnoreCancellationAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
            // Expected during cleanup.
        }
    }
}
