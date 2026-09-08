using System.Diagnostics;
using LauncherGo.Abstractions.Services;
using LauncherGo.Domains.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LauncherGo.Services;

/// <summary>
///     执行实例启动/停止过程中的自动化清理和 Windows 批处理脚本。
/// </summary>
public sealed class AutomationLifecycleService : IAutomationLifecycleService
{
    private static readonly TimeSpan ScriptTimeout = TimeSpan.FromMinutes(10);
    private readonly IAutomationSettingsService _settingsService;
    private readonly ILogger<AutomationLifecycleService> _logger;

    public AutomationLifecycleService(
        IAutomationSettingsService settingsService,
        ILogger<AutomationLifecycleService>? logger = null)
    {
        _settingsService = settingsService;
        _logger = logger ?? NullLogger<AutomationLifecycleService>.Instance;
    }

    public async Task ExecuteAsync(
        InstanceProfile profile,
        AutomationScriptTrigger trigger,
        CancellationToken cancellationToken = default)
    {
        if (profile is null)
            throw new ArgumentNullException(nameof(profile));

        var settings = await _settingsService.LoadAsync(profile, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (trigger == AutomationScriptTrigger.BeforeStart && settings.ClearCacheBeforeStart)
        {
            ClearProfileCache(profile);
        }

        if (!settings.AutomationScriptsEnabled)
            return;

        foreach (var script in (settings.AutomationScripts ?? [])
                     .Where(item => item is not null && item.Enabled && item.Trigger == trigger))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await RunScriptAsync(profile, script, cancellationToken).ConfigureAwait(false);
        }
    }

    internal static void ClearProfileCache(InstanceProfile profile)
    {
        var profilePath = WorkspacePathHelper.ResolveProfileDataPath(profile.DirectoryPath);
        if (string.IsNullOrWhiteSpace(profilePath) || !Directory.Exists(profilePath))
            return;

        var cachePath = Path.Combine(profilePath, "Cache");
        if (!Directory.Exists(cachePath))
            return;

        foreach (var file in Directory.EnumerateFiles(cachePath, "*", SearchOption.TopDirectoryOnly))
        {
            File.SetAttributes(file, FileAttributes.Normal);
            File.Delete(file);
        }

        foreach (var directory in Directory.EnumerateDirectories(cachePath, "*", SearchOption.TopDirectoryOnly))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private async Task RunScriptAsync(
        InstanceProfile profile,
        AutomationScript script,
        CancellationToken cancellationToken)
    {
        var path = script.ScriptPath?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            path = Path.GetFullPath(path);
        }
        catch
        {
            _logger.LogWarning("自动化脚本路径无效：{ScriptPath}", path);
            return;
        }

        if (!File.Exists(path) ||
            (!path.EndsWith(".bat", StringComparison.OrdinalIgnoreCase) &&
             !path.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)))
        {
            _logger.LogWarning("自动化脚本不存在或不是 .bat/.cmd 文件：{ScriptPath}", path);
            return;
        }

        if (!OperatingSystem.IsWindows())
        {
            _logger.LogWarning("当前平台不支持 Windows 自动化脚本：{ScriptPath}", path);
            return;
        }

        var commandShell = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
        var startInfo = new ProcessStartInfo
        {
            FileName = commandShell,
            WorkingDirectory = Path.GetDirectoryName(path) ?? profile.DirectoryPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("/d");
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add(path);

        using var process = new Process { StartInfo = startInfo };
        _logger.LogInformation(
            "执行自动化脚本。ProfileId={ProfileId}, Trigger={Trigger}, ScriptPath={ScriptPath}",
            profile.Id,
            script.Trigger,
            path);

        Task<string>? stdoutTask = null, stderrTask = null;
        string stdout, stderr;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!process.Start())
                throw new InvalidOperationException($"启动自动化脚本失败：{path}");
            _logger.LogInformation("Automation script process started. ProfileId={ProfileId}, ProcessId={ProcessId}, Trigger={Trigger}.",
                profile.Id, process.Id, script.Trigger);
            cancellationToken.ThrowIfCancellationRequested();
            stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).WaitAsync(ScriptTimeout, cancellationToken).ConfigureAwait(false);
            stdout = await stdoutTask.ConfigureAwait(false);
            stderr = await stderrTask.ConfigureAwait(false);
        }
        catch
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception error) { _logger.LogDebug(error, "Automation process cleanup failed. ProfileId={ProfileId}.", profile.Id); }
            // Read tasks may fault independently when a cancelled process closes its pipes.
            if (stdoutTask is not null) _ = ObserveReadAsync(stdoutTask);
            if (stderrTask is not null) _ = ObserveReadAsync(stderrTask);
            throw;
        }
        if (!string.IsNullOrWhiteSpace(stdout))
            _logger.LogInformation("自动化脚本输出：{Output}", stdout.Trim());
        if (!string.IsNullOrWhiteSpace(stderr))
            _logger.LogWarning("自动化脚本错误输出：{Output}", stderr.Trim());
        if (process.ExitCode != 0)
        {
            _logger.LogWarning("自动化脚本退出码为 {ExitCode}：{ScriptPath}", process.ExitCode, path);
        }
    }

    private static async Task ObserveReadAsync(Task task)
    {
        try { await task.ConfigureAwait(false); } catch { }
    }
}
