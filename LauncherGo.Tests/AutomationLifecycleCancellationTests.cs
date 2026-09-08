using System.Diagnostics;
using LauncherGo.Abstractions.Services;
using LauncherGo.Domains.Models;
using LauncherGo.Services;
using Microsoft.Extensions.Logging;
using Xunit;

namespace LauncherGo.Tests;

public sealed class AutomationLifecycleCancellationTests
{
    [Fact]
    public async Task CancellationTerminatesOwnedScriptProcess()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), $"launchergo-script-cancel-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var script = Path.Combine(root, "wait.cmd");
        // A loopback-only wait; no user scripts or server processes are involved.
        await File.WriteAllTextAsync(script, "@echo off\r\nping.exe 127.0.0.1 -n 30 >nul\r\n");
        var settings = new AutomationSettings
        {
            AutomationScriptsEnabled = true,
            AutomationScripts = [new() { ScriptPath = script, Trigger = AutomationScriptTrigger.BeforeStart }]
        };
        var logger = new ProcessLogger();
        var service = new AutomationLifecycleService(new SettingsStub(settings), logger);
        using var cts = new CancellationTokenSource();
        Process? process = null;
        Task? execution = null;
        try
        {
            execution = service.ExecuteAsync(new InstanceProfile { Id = "script-test", DirectoryPath = root }, AutomationScriptTrigger.BeforeStart, cts.Token);
            var pid = await logger.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            process = Process.GetProcessById(pid);
            _ = process.Handle;
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution.WaitAsync(TimeSpan.FromSeconds(10)));
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(process.HasExited);
        }
        finally
        {
            cts.Cancel();
            if (process is not null)
            {
                if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
                process.Dispose();
            }
            if (execution is not null) { try { await execution; } catch { } }
            Directory.Delete(root, true);
        }
    }

    private sealed class ProcessLogger : ILogger<AutomationLifecycleService>
    {
        internal TaskCompletionSource<int> Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (state is IEnumerable<KeyValuePair<string, object?>> fields)
                foreach (var field in fields)
                    if (field.Key == "ProcessId" && field.Value is int pid) Started.TrySetResult(pid);
        }
    }

    private sealed class SettingsStub(AutomationSettings settings) : IAutomationSettingsService
    {
        public Task<AutomationSettings> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(settings);
        public Task<AutomationSettings> LoadAsync(InstanceProfile profile, CancellationToken cancellationToken = default) => Task.FromResult(settings);
        public Task<IReadOnlyList<AutomationSettings>> LoadAllAsync(IReadOnlyList<InstanceProfile> profiles, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SaveAsync(AutomationSettings value, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SaveAsync(InstanceProfile profile, AutomationSettings value, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public string GetSettingsPath(InstanceProfile profile) => throw new NotSupportedException();
    }
}
