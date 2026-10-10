using System.Reflection;
using LauncherGo.Abstractions.Services;
using LauncherGo.Domains.Models;
using LauncherGo.Ui.Services;
using Xunit;

namespace LauncherGo.Tests;

public sealed class ServerAuthReloadTests
{
    [Fact]
    public async Task StoppedServer_DoesNotSendReload()
    {
        var process = FakeProcess.Create();
        process.Status = new ServerRuntimeStatus();
        Assert.False(await ServerAuthReload.ReloadAsync(process.Service, "one"));
        Assert.Empty(process.Commands);
        Assert.Null(process.Output);
    }

    [Fact]
    public async Task UnavailableCommandChannel_IsReported()
    {
        var process = FakeProcess.Create();
        process.Status = new ServerRuntimeStatus { IsRunning = true };
        await Assert.ThrowsAsync<InvalidOperationException>(() => ServerAuthReload.ReloadAsync(process.Service, "one"));
        Assert.Empty(process.Commands);
    }

    [Fact]
    public async Task Reload_WaitsForMatchingProfileAndRequestAndUnsubscribes()
    {
        var process = FakeProcess.Create();
        var reload = ServerAuthReload.ReloadAsync(process.Service, "one");
        var command = Assert.Single(process.Commands);
        Assert.StartsWith("/serverauth reload ", command);
        process.Emit("other", command, "OK");
        process.Emit("one", "/serverauth reload unrelated", "OK");
        Assert.False(reload.IsCompleted);
        process.Emit("one", command, "OK");
        Assert.True(await reload);
        Assert.Null(process.Output);
    }

    [Fact]
    public async Task Reload_ReportsServerErrorAndUnsubscribes()
    {
        var process = FakeProcess.Create();
        var reload = ServerAuthReload.ReloadAsync(process.Service, "one");
        process.Emit("one", Assert.Single(process.Commands), "ERROR Invalid config");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => reload);
        Assert.Equal("Invalid config", error.Message);
        Assert.Null(process.Output);
    }

    [Fact]
    public async Task MissingResponse_ReportsTimeoutAndUnsubscribes()
    {
        var process = FakeProcess.Create();
        var error = await Assert.ThrowsAsync<TimeoutException>(() =>
            ServerAuthReload.ReloadAsync(process.Service, "one", timeout: TimeSpan.FromMilliseconds(20)));
        Assert.Contains("1.1.1", error.Message);
        Assert.Null(process.Output);
    }

    [Fact]
    public async Task Cancellation_Unsubscribes()
    {
        var process = FakeProcess.Create();
        using var cancellation = new CancellationTokenSource();
        var reload = ServerAuthReload.ReloadAsync(process.Service, "one", cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reload);
        Assert.Null(process.Output);
    }

    public class FakeProcess : DispatchProxy
    {
        public IServerProcessService Service { get; private set; } = null!;
        public ServerRuntimeStatus Status { get; set; } = new() { IsRunning = true, CanSendCommands = true };
        public List<string> Commands { get; } = [];
        public EventHandler<ServerOutputLine>? Output { get; private set; }

        public static FakeProcess Create()
        {
            var service = DispatchProxy.Create<IServerProcessService, FakeProcess>();
            var fake = (FakeProcess)service;
            fake.Service = service;
            return fake;
        }

        public void Emit(string profileId, string command, string result)
        {
            var requestId = command.Split(' ')[2];
            Output?.Invoke(this, new ServerOutputLine
            {
                ProfileId = profileId,
                Line = "[Notification] [SERVER-AUTH] reload " + requestId + " " + result
            });
        }

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            switch (method!.Name)
            {
                case "GetCurrentStatus": return Status;
                case "SendCommandAsync": Commands.Add((string)args![1]!); return Task.CompletedTask;
                case "add_ProfileOutputReceived": Output += (EventHandler<ServerOutputLine>)args![0]!; return null;
                case "remove_ProfileOutputReceived": Output -= (EventHandler<ServerOutputLine>)args![0]!; return null;
                default: throw new NotSupportedException(method.Name);
            }
        }
    }
}
