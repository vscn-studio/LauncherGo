using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using LauncherGo.Abstractions.Services;
using LauncherGo.Domains.Models;

namespace LauncherGo.Services;

/// <summary>
///     自动化调度服务（定时开关服/备份/播报/日志导出）
/// </summary>
public partial class AutomationService : IAutomationService, IDisposable
{
    private readonly IAutomationSettingsService _settingsService;
    private readonly IInstanceProfileService _profileService;
    private readonly IInstanceSaveService _instanceSaveService;
    private readonly ILogTailService _logTailService;
    private readonly IServerProcessService _serverProcessService;
    private readonly SemaphoreSlim _backupGate = new(1, 1);
    private readonly object _logsGate = new();
    private readonly object _backupStateGate = new();
    private readonly List<string> _runtimeLogs = [];
    private readonly ConcurrentQueue<string> _latestServerLines = new();
    private readonly HashSet<string> _executedMinuteKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loopTask;

    private const int MaxRuntimeLogs = 1500;
    private const int MaxServerLines = 5000;

    private IReadOnlyList<AutomationSettings> _settings = [];
    private DateTime _lastTickMinute = DateTime.MinValue;
    private readonly Dictionary<string, bool> _lastDesiredServerRunningByProfile = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _lastDesiredServerRunningInitializedProfiles = new(StringComparer.OrdinalIgnoreCase);
    private TaskCompletionSource<bool>? _backupCompletionSource;
    private string? _backupCompletionProfileId;
    private static readonly TimeSpan BackupWaitTimeout = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan BackupFileProbeInterval = TimeSpan.FromMilliseconds(500);
    private const int RequiredBackupFileStableSamples = 2;

    private enum GeneratedBackupWaitResult
    {
        Ready,
        Failed,
        TimedOut
    }

    public event EventHandler<string>? RuntimeLogReceived;

    public AutomationService(
        IAutomationSettingsService settingsService,
        IInstanceProfileService profileService,
        IInstanceSaveService instanceSaveService,
        ILogTailService logTailService,
        IServerProcessService serverProcessService)
    {
        _settingsService = settingsService;
        _profileService = profileService;
        _instanceSaveService = instanceSaveService;
        _logTailService = logTailService;
        _serverProcessService = serverProcessService;

        _serverProcessService.OutputReceived += OnServerOutputReceived;
        _serverProcessService.ProfileOutputReceived += OnServerProfileOutputReceived;
        _logTailService.LogLineReceived += OnLogTailLineReceived;
        _serverProcessService.StatusChanged += OnServerStatusChanged;
        _loopTask = Task.Run(() => LoopAsync(_cts.Token), CancellationToken.None);
    }

    public IReadOnlyList<string> GetRuntimeLogs()
    {
        lock (_logsGate)
        {
            return _runtimeLogs.ToList();
        }
    }

    public async Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        var profiles = _profileService.GetProfiles();
        _settings = await _settingsService.LoadAllAsync(profiles, cancellationToken);
        _lastDesiredServerRunningByProfile.Clear();
        _lastDesiredServerRunningInitializedProfiles.Clear();
        WriteRuntimeLog("已重新加载自动化设置。");
    }

    public void Dispose()
    {
        _cts.Cancel();
        _serverProcessService.OutputReceived -= OnServerOutputReceived;
        _serverProcessService.ProfileOutputReceived -= OnServerProfileOutputReceived;
        _logTailService.LogLineReceived -= OnLogTailLineReceived;
        _serverProcessService.StatusChanged -= OnServerStatusChanged;
        try
        {
            _loopTask.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // ignored
        }
        _cts.Dispose();
    }

    private async Task LoopAsync(CancellationToken cancellationToken)
    {
        await ReloadAsync(cancellationToken);
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                WriteRuntimeLog($"自动化循环异常：{ex.Message}");
            }

            var delay = TimeSpan.FromSeconds(Math.Max(1, 60 - DateTime.Now.Second));
            await Task.Delay(delay, cancellationToken);
        }
    }

    private async Task TickAsync(CancellationToken cancellationToken)
    {
        var now = DateTime.Now;
        var minute = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0);
        if (minute == _lastTickMinute)
            return;

        _lastTickMinute = minute;
        PurgeExecutedKeys(minute);

        foreach (var settings in _settings)
        {
            var profile = ResolveTargetProfile(settings);
            if (profile is null)
            {
                continue;
            }

            if (settings.RestartSchedulerEnabled)
                await HandleRestartWindowsAsync(settings, profile, minute, cancellationToken);

            if (settings.BackupEnabled)
                await HandleBackupAsync(settings, profile, minute, cancellationToken);

            if (settings.BroadcastEnabled)
                await HandleBroadcastAsync(settings, profile, minute, cancellationToken);

            if (settings.CommandEnabled)
                await HandleScheduledCommandsAsync(settings, profile, minute, cancellationToken);

            if (settings.ExportLogEnabled)
                await HandleExportLogsAsync(settings, profile, minute, cancellationToken);
        }
    }

    private async Task HandleRestartWindowsAsync(
        AutomationSettings settings,
        InstanceProfile profile,
        DateTime minute,
        CancellationToken cancellationToken)
    {
        var enabledWindows = (settings.ActionWindows ?? [])
            .Where(window => window.Enabled)
            .ToList();
        if (enabledWindows.Count == 0)
        {
            enabledWindows = (settings.TimeWindows ?? [])
                .Where(window => window.Enabled)
                .Select(window => new AutomationActionWindow
                {
                    ScheduleMode = AutomationScheduleMode.Weekly,
                    StartDayOfWeek = 1,
                    EndDayOfWeek = 7,
                    StartTime = window.StartTime,
                    EndTime = window.EndTime,
                    Action = AutomationActionType.Start,
                    Enabled = window.Enabled
                })
                .ToList();
        }

        var conflict = FindConflict(enabledWindows, minute);
        if (conflict is not null)
        {
            var conflictKey = $"{profile.Id}|conflict|{minute:yyyyMMddHHmm}|{conflict}";
            if (MarkExecuted(conflictKey))
            {
                WriteRuntimeLog($"自动化计划冲突（档案：{profile.Name}），已跳过本分钟：{conflict}");
            }

            return;
        }

        var desiredRunning = ComputeDesiredServerRunning(enabledWindows, minute);
        if (!_lastDesiredServerRunningInitializedProfiles.Contains(profile.Id))
        {
            _lastDesiredServerRunningByProfile[profile.Id] = desiredRunning;
            _lastDesiredServerRunningInitializedProfiles.Add(profile.Id);
        }

        var status = _serverProcessService.GetCurrentStatus(profile.Id);
        var lastDesired = _lastDesiredServerRunningByProfile.TryGetValue(profile.Id, out var value) && value;
        if (lastDesired != desiredRunning || status.IsRunning != desiredRunning)
        {
            var changeKey = $"{profile.Id}|desired|{minute:yyyyMMddHHmm}|{desiredRunning}";
            if (MarkExecuted(changeKey))
            {
                if (desiredRunning)
                {
                    await EnsureServerStartedAsync(profile, cancellationToken);
                }
                else
                {
                    await EnsureServerStoppedAsync(settings, profile, cancellationToken);
                }
            }
        }

        _lastDesiredServerRunningByProfile[profile.Id] = desiredRunning;
    }

    private async Task HandleBroadcastAsync(
        AutomationSettings settings,
        InstanceProfile profile,
        DateTime minute,
        CancellationToken cancellationToken)
    {
        foreach (var item in settings.BroadcastMessages.Where(x => x.Enabled))
        {
            if (!TryParseHm(item.Time, out var at))
                continue;

            var point = minute.Date.Add(at);
            if (point != minute)
                continue;

            var key = $"{profile.Id}|broadcast|{minute:yyyyMMddHHmm}|{item.Message}";
            if (!MarkExecuted(key))
                continue;

            await TryBroadcastSystemMessageAsync(profile, item.Message, cancellationToken);
        }
    }

    private async Task HandleBackupAsync(
        AutomationSettings settings,
        InstanceProfile profile,
        DateTime minute,
        CancellationToken cancellationToken)
    {
        foreach (var schedule in settings.BackupSchedules.Where(item => item.Enabled))
        {
            if (!BackupScheduleCalculator.IsDue(schedule, minute))
                continue;

            var key = $"{profile.Id}|backup|{schedule.Id}|{minute:yyyyMMddHHmm}";
            if (!MarkExecuted(key))
                continue;

            await TryBackupActiveSaveAsync(profile, settings.BackupRetentionCount, cancellationToken);
        }
    }

    private async Task HandleScheduledCommandsAsync(
        AutomationSettings settings,
        InstanceProfile profile,
        DateTime minute,
        CancellationToken cancellationToken)
    {
        foreach (var item in settings.ScheduledCommands.Where(x => x.Enabled))
        {
            var schedule = item.Schedule ?? new BackupSchedule
            {
                Type = BackupScheduleType.Daily,
                Time = item.Time,
                AnchorDate = minute.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            };
            if (!BackupScheduleCalculator.IsDue(schedule, minute))
                continue;

            var key = $"{profile.Id}|command|{schedule.Id}|{minute:yyyyMMddHHmm}|{item.Command}";
            if (!MarkExecuted(key))
                continue;

            await TrySendScheduledCommandAsync(profile, item.Command, cancellationToken);
        }
    }

    private async Task HandleExportLogsAsync(
        AutomationSettings settings,
        InstanceProfile profile,
        DateTime minute,
        CancellationToken cancellationToken)
    {
        foreach (var time in settings.ExportTimes)
        {
            if (!TryParseHm(time, out var at))
                continue;

            var point = minute.Date.Add(at);
            if (point != minute)
                continue;

            var key = $"{profile.Id}|export|{minute:yyyyMMddHHmm}|{time}";
            if (!MarkExecuted(key))
                continue;

            await ExportLogsAsync(settings, profile, "scheduled", cancellationToken);
        }
    }

    private async Task EnsureServerStartedAsync(InstanceProfile profile, CancellationToken cancellationToken)
    {
        var status = _serverProcessService.GetCurrentStatus(profile.Id);
        if (status.IsRunning)
        {
            WriteRuntimeLog($"自动化：服务端已在运行，跳过开服（档案：{profile.Name}）。");
            return;
        }

        await _serverProcessService.StartAsync(profile, cancellationToken);
        WriteRuntimeLog($"自动化：已按计划启动服务端（档案：{profile.Name}）。");
    }

    private async Task EnsureServerStoppedAsync(
        AutomationSettings settings,
        InstanceProfile profile,
        CancellationToken cancellationToken)
    {
        if (settings.BackupBeforeShutdown)
        {
            await TryBackupActiveSaveAsync(profile, settings.BackupRetentionCount, cancellationToken);
        }

        if (settings.ExportBeforeShutdown)
            await ExportLogsAsync(settings, profile, "before-shutdown", cancellationToken);

        var status = _serverProcessService.GetCurrentStatus(profile.Id);
        if (!status.IsRunning)
        {
            WriteRuntimeLog($"自动化：服务端未运行，跳过关服（档案：{profile.Name}）。");
            return;
        }

        await _serverProcessService.StopAsync(profile.Id, TimeSpan.FromSeconds(15), cancellationToken);
        WriteRuntimeLog($"自动化：已按计划关闭服务端（档案：{profile.Name}）。");
    }

    private async Task TryBackupActiveSaveAsync(
        InstanceProfile profile,
        int retentionCount,
        CancellationToken cancellationToken)
    {
        await _backupGate.WaitAsync(cancellationToken);
        try
        {
            var status = _serverProcessService.GetCurrentStatus(profile.Id);
            var requestedBackupFileName = BuildBackupFileName(profile);
            if (status.IsRunning)
            {
                var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                lock (_backupStateGate)
                {
                    _backupCompletionSource = completion;
                    _backupCompletionProfileId = profile.Id;
                }

                var command = $"/genbackup {requestedBackupFileName}";
                await _serverProcessService.SendCommandAsync(profile.Id, command, cancellationToken);
                WriteRuntimeLog($"自动化备份：已请求服务器备份（档案：{profile.Name}）。");

                // /genbackup completion may be sent only to the command caller, not the server console.
                var generatedBackupPath = Path.Combine(profile.DirectoryPath, "Backups", requestedBackupFileName);
                var waitResult = await WaitForGeneratedBackupFileAsync(
                    generatedBackupPath,
                    completion.Task,
                    cancellationToken);
                switch (waitResult)
                {
                    case GeneratedBackupWaitResult.Ready:
                        WriteRuntimeLog("自动化备份：已确认备份文件已生成。");
                        var result = await _instanceSaveService.FinalizeManagedBackupAsync(
                            profile,
                            generatedBackupPath,
                            retentionCount,
                            cancellationToken);
                        WriteManagedBackupResult(result);
                        break;
                    case GeneratedBackupWaitResult.Failed:
                        WriteRuntimeLog("自动化备份：服务器备份失败。");
                        break;
                    default:
                        WriteRuntimeLog("自动化备份：等待备份文件生成超时。");
                        break;
                }

                return;
            }

            var offlineResult = await _instanceSaveService.BackupManagedActiveSaveAsync(
                profile,
                requestedBackupFileName,
                retentionCount,
                cancellationToken);
            WriteRuntimeLog($"自动化备份：已备份当前存档（{Path.GetFileName(offlineResult.SourcePath)}）。");
            WriteManagedBackupResult(offlineResult);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            WriteRuntimeLog($"自动化备份失败：{ex.Message}");
        }
        finally
        {
            lock (_backupStateGate)
            {
                _backupCompletionSource = null;
                _backupCompletionProfileId = null;
            }

            _backupGate.Release();
        }
    }

    private async Task<GeneratedBackupWaitResult> WaitForGeneratedBackupFileAsync(
        string backupFilePath,
        Task<bool> outputCompletionTask,
        CancellationToken cancellationToken)
    {
        using var waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var fileReadyTask = WaitForBackupFileReadyAsync(
            backupFilePath,
            BackupWaitTimeout,
            BackupFileProbeInterval,
            waitCancellation.Token);
        var firstCompletedTask = await Task.WhenAny(fileReadyTask, outputCompletionTask);
        if (firstCompletedTask == outputCompletionTask && !await outputCompletionTask)
        {
            if (fileReadyTask.IsCompletedSuccessfully && fileReadyTask.Result)
            {
                return GeneratedBackupWaitResult.Ready;
            }

            waitCancellation.Cancel();
            try
            {
                await fileReadyTask;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // The server rejected the backup request, so the file probe is no longer needed.
            }

            return GeneratedBackupWaitResult.Failed;
        }

        return await fileReadyTask
            ? GeneratedBackupWaitResult.Ready
            : GeneratedBackupWaitResult.TimedOut;
    }

    internal static async Task<bool> WaitForBackupFileReadyAsync(
        string backupFilePath,
        TimeSpan timeout,
        TimeSpan probeInterval,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(backupFilePath) || timeout <= TimeSpan.Zero)
        {
            return false;
        }

        if (probeInterval <= TimeSpan.Zero)
        {
            probeInterval = TimeSpan.FromMilliseconds(1);
        }

        var deadline = DateTimeOffset.UtcNow + timeout;
        long? lastLength = null;
        var lastWriteTimeUtc = DateTime.MinValue;
        var stableSampleCount = 0;

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (TryReadBackupFileMetadata(backupFilePath, out var length, out var writeTimeUtc))
            {
                if (lastLength == length && lastWriteTimeUtc == writeTimeUtc)
                {
                    stableSampleCount++;
                }
                else
                {
                    lastLength = length;
                    lastWriteTimeUtc = writeTimeUtc;
                    stableSampleCount = 1;
                }

                if (stableSampleCount >= RequiredBackupFileStableSamples)
                {
                    return true;
                }
            }
            else
            {
                lastLength = null;
                lastWriteTimeUtc = DateTime.MinValue;
                stableSampleCount = 0;
            }

            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                break;
            }

            await Task.Delay(remaining < probeInterval ? remaining : probeInterval, cancellationToken);
        }

        return false;
    }

    private static bool TryReadBackupFileMetadata(
        string backupFilePath,
        out long length,
        out DateTime writeTimeUtc)
    {
        length = 0;
        writeTimeUtc = DateTime.MinValue;
        try
        {
            using var stream = new FileStream(
                backupFilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                1,
                FileOptions.SequentialScan);
            if (stream.Length <= 0)
            {
                return false;
            }

            var fileInfo = new FileInfo(backupFilePath);
            fileInfo.Refresh();
            if (!fileInfo.Exists || fileInfo.Length != stream.Length)
            {
                return false;
            }

            length = stream.Length;
            writeTimeUtc = fileInfo.LastWriteTimeUtc;
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private void WriteManagedBackupResult(ManagedBackupResult result)
    {
        if (!string.IsNullOrWhiteSpace(result.CompressionError))
        {
            WriteRuntimeLog($"自动化备份已完成，但压缩失败：{result.CompressionError}");
        }
        else if (result.CompressionEnabled && result.CompressedPath is not null)
        {
            var action = result.CompressionSkipped ? "已是最新，跳过" : "已生成";
            var deletedText = result.SourceDeleted ? "，已删除原始 vcdbs" : string.Empty;
            WriteRuntimeLog($"自动化备份压缩：{action} {Path.GetFileName(result.CompressedPath)}{deletedText}。");
        }

        if (result.RemovedCount > 0)
            WriteRuntimeLog($"自动化备份轮换：已移除最早的 {result.RemovedCount} 份备份。");
    }

    private static string BuildBackupFileName(InstanceProfile profile)
    {
        var sourceName = Path.GetFileNameWithoutExtension(profile.ActiveSaveFile);
        if (string.IsNullOrWhiteSpace(sourceName))
        {
            sourceName = "world";
        }

        var safeName = string.Join(
            '_',
            sourceName.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
        if (string.IsNullOrWhiteSpace(safeName))
        {
            safeName = "world";
        }

        safeName = string.Concat(safeName.Select(character => char.IsWhiteSpace(character) ? '_' : character));

        var suffix = Guid.NewGuid().ToString("N")[..8];
        return $"launchergo-backup-{safeName}-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}-{suffix}.vcdbs";
    }

    private async Task TryBroadcastSystemMessageAsync(
        InstanceProfile profile,
        string content,
        CancellationToken cancellationToken)
    {
        var status = _serverProcessService.GetCurrentStatus(profile.Id);
        if (!status.IsRunning)
        {
            WriteRuntimeLog($"自动化播报跳过（档案 {profile.Name} 未运行）：{content}");
            return;
        }

        var normalized = content.Replace('\r', ' ').Replace('\n', ' ').Trim();
        await _serverProcessService.SendCommandAsync(profile.Id, $"/announce {normalized}", cancellationToken);
        WriteRuntimeLog($"自动化播报（档案：{profile.Name}）：{content}");
    }

    private async Task TrySendScheduledCommandAsync(
        InstanceProfile profile,
        string command,
        CancellationToken cancellationToken)
    {
        var status = _serverProcessService.GetCurrentStatus(profile.Id);
        if (!status.IsRunning)
        {
            WriteRuntimeLog($"自动化命令跳过（档案 {profile.Name} 未运行）：{command}");
            return;
        }

        var normalized = command.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            return;

        await _serverProcessService.SendCommandAsync(profile.Id, normalized, cancellationToken);
        WriteRuntimeLog($"自动化命令（档案：{profile.Name}）：{normalized}");
    }

    private async Task ExportLogsAsync(
        AutomationSettings settings,
        InstanceProfile profile,
        string reason,
        CancellationToken cancellationToken)
    {
        var exportRoot = Path.Combine(WorkspacePathHelper.WorkspaceRoot, "exports", "automation");
        Directory.CreateDirectory(exportRoot);
        var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var profileToken = SanitizeFileName(profile.Name);

        var sourceLines = _latestServerLines.ToArray();
        if (sourceLines.Length == 0)
        {
            WriteRuntimeLog("自动化日志导出跳过：当前未采集到控制台输出。");
            return;
        }

        var allPath = Path.Combine(exportRoot, $"automation-{profileToken}-{reason}-{timestamp}-all.log");
        await File.WriteAllLinesAsync(allPath, sourceLines, cancellationToken);

        if (settings.ExportIncludeChat)
        {
            var chatLines = sourceLines.Where(IsChatLine).ToList();
            var chatPath = Path.Combine(exportRoot, $"automation-{profileToken}-{reason}-{timestamp}-chat.log");
            await File.WriteAllLinesAsync(chatPath, chatLines, cancellationToken);
        }

        if (settings.ExportIncludeServerInfo)
        {
            var infoLines = sourceLines.Where(IsServerInfoLine).ToList();
            var infoPath = Path.Combine(exportRoot, $"automation-{profileToken}-{reason}-{timestamp}-server.log");
            await File.WriteAllLinesAsync(infoPath, infoLines, cancellationToken);
        }

        WriteRuntimeLog($"自动化日志已导出：{allPath}");
    }

    private static bool IsChatLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return false;

        return ChatRegex().IsMatch(line);
    }

    private static bool IsServerInfoLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return false;

        return !IsChatLine(line);
    }

    private void OnServerOutputReceived(object? sender, string line)
    {
        _latestServerLines.Enqueue(line);
        while (_latestServerLines.Count > MaxServerLines)
            _latestServerLines.TryDequeue(out _);
    }

    private void OnServerProfileOutputReceived(object? sender, ServerOutputLine output)
    {
        TryCompleteBackupWatcher(output.ProfileId, output.Line);
    }

    private void OnLogTailLineReceived(object? sender, string line)
    {
        _latestServerLines.Enqueue($"[log] {line}");
        while (_latestServerLines.Count > MaxServerLines)
            _latestServerLines.TryDequeue(out _);
    }

    private void OnServerStatusChanged(object? sender, ServerRuntimeStatus status)
    {
        var state = status.IsRunning ? "运行中" : "未运行";
        var profileName = string.IsNullOrWhiteSpace(status.ProfileId)
            ? "未识别档案"
            : _profileService.GetProfileById(status.ProfileId)?.Name ?? status.ProfileId;
        WriteRuntimeLog($"服务端状态更新（档案：{profileName}）：{state}，在线 {status.OnlinePlayers}。");
    }

    private InstanceProfile? ResolveTargetProfile(AutomationSettings settings)
    {
        var profiles = _profileService.GetProfiles();
        if (profiles.Count == 0)
        {
            WriteRuntimeLog("自动化未找到档案，无法执行计划。");
            return null;
        }

        if (!string.IsNullOrWhiteSpace(settings.TargetProfileId))
        {
            var matched = profiles.FirstOrDefault(profile =>
                profile.Id.Equals(settings.TargetProfileId, StringComparison.OrdinalIgnoreCase));
            if (matched is not null)
                return matched;
        }

        return profiles[0];
    }

    private static string SanitizeFileName(string value)
    {
        var sanitized = string.IsNullOrWhiteSpace(value) ? "server" : value.Trim();
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            sanitized = sanitized.Replace(invalid, '_');
        }

        return sanitized;
    }

    private void WriteRuntimeLog(string line)
    {
        var text = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {line}";
        lock (_logsGate)
        {
            _runtimeLogs.Add(text);
            while (_runtimeLogs.Count > MaxRuntimeLogs)
                _runtimeLogs.RemoveAt(0);
        }

        RuntimeLogReceived?.Invoke(this, text);
    }

    private bool MarkExecuted(string key)
    {
        lock (_executedMinuteKeys)
        {
            return _executedMinuteKeys.Add(key);
        }
    }

    private void PurgeExecutedKeys(DateTime minute)
    {
        var dayKey = minute.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        lock (_executedMinuteKeys)
        {
            _executedMinuteKeys.RemoveWhere(key => !key.Contains(dayKey, StringComparison.OrdinalIgnoreCase));
        }
    }

    private static bool TryParseHm(string? value, out TimeSpan result)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            result = default;
            return false;
        }

        if (TimeSpan.TryParseExact(value.Trim(), @"hh\:mm", CultureInfo.InvariantCulture, out result))
            return true;

        return TimeSpan.TryParse(value.Trim(), out result);
    }

    private static string? FindConflict(IReadOnlyList<AutomationActionWindow> windows, DateTime minute)
    {
        var desiredActions = windows
            .Where(window => IsWindowActive(window, minute))
            .Select(window => window.Action)
            .Distinct()
            .ToList();
        if (desiredActions.Count <= 1)
        {
            return null;
        }

        return "same time has both start and stop actions";
    }

    private static bool ComputeDesiredServerRunning(IReadOnlyList<AutomationActionWindow> windows, DateTime minute)
    {
        var activeWindows = windows
            .Where(window => IsWindowActive(window, minute))
            .ToList();
        if (activeWindows.Count == 0)
        {
            return false;
        }

        var hasStart = activeWindows.Any(window => window.Action == AutomationActionType.Start);
        var hasStop = activeWindows.Any(window => window.Action == AutomationActionType.Stop);
        if (hasStart && !hasStop)
        {
            return true;
        }

        if (hasStop && !hasStart)
        {
            return false;
        }

        // Conflict case already guarded by FindConflict; default to stop for safety.
        return false;
    }

    private static bool IsWindowActive(AutomationActionWindow window, DateTime minute)
    {
        if (!TryParseHm(window.StartTime, out var start) || !TryParseHm(window.EndTime, out var end))
        {
            return false;
        }

        var minuteOfDay = minute.TimeOfDay;
        var inTimeRange = IsTimeInRange(minuteOfDay, start, end);
        if (!inTimeRange)
        {
            return false;
        }

        return window.ScheduleMode switch
        {
            AutomationScheduleMode.Weekly => IsWeekDayInRange(minute.DayOfWeek, window.StartDayOfWeek, window.EndDayOfWeek),
            AutomationScheduleMode.DateRange => IsDateInRange(minute.Date, window.StartDate, window.EndDate),
            _ => false
        };
    }

    private static bool IsTimeInRange(TimeSpan time, TimeSpan start, TimeSpan end)
    {
        if (start == end)
        {
            return true;
        }

        if (start < end)
        {
            return time >= start && time < end;
        }

        // Cross-day window, e.g. 23:00-06:00
        return time >= start || time < end;
    }

    private static bool IsWeekDayInRange(DayOfWeek day, int startDay, int endDay)
    {
        var dayValue = ToIsoWeekDay(day);
        startDay = NormalizeWeekDay(startDay);
        endDay = NormalizeWeekDay(endDay);

        if (startDay <= endDay)
        {
            return dayValue >= startDay && dayValue <= endDay;
        }

        // Wrap range: e.g. Fri->Mon
        return dayValue >= startDay || dayValue <= endDay;
    }

    private static bool IsDateInRange(DateTime date, string startDateRaw, string endDateRaw)
    {
        if (!DateOnly.TryParse(startDateRaw, out var startDate) || !DateOnly.TryParse(endDateRaw, out var endDate))
        {
            return false;
        }

        var day = DateOnly.FromDateTime(date);
        if (startDate <= endDate)
        {
            return day >= startDate && day <= endDate;
        }

        return day >= startDate || day <= endDate;
    }

    private static int ToIsoWeekDay(DayOfWeek day)
    {
        return day switch
        {
            DayOfWeek.Monday => 1,
            DayOfWeek.Tuesday => 2,
            DayOfWeek.Wednesday => 3,
            DayOfWeek.Thursday => 4,
            DayOfWeek.Friday => 5,
            DayOfWeek.Saturday => 6,
            DayOfWeek.Sunday => 7,
            _ => 1
        };
    }

    private static int NormalizeWeekDay(int day)
    {
        return day is >= 1 and <= 7 ? day : 1;
    }

    private void TryCompleteBackupWatcher(string profileId, string line)
    {
        TaskCompletionSource<bool>? completionSource;
        lock (_backupStateGate)
        {
            if (string.IsNullOrWhiteSpace(_backupCompletionProfileId) ||
                !string.Equals(_backupCompletionProfileId, profileId, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            completionSource = _backupCompletionSource;
        }

        if (completionSource is null)
            return;

        if (IsBackupCompletedLine(line))
        {
            completionSource.TrySetResult(true);
            return;
        }

        if (IsBackupFailedLine(line))
        {
            completionSource.TrySetResult(false);
        }
    }

    internal static bool IsBackupCompletedLine(string? line)
    {
        return !string.IsNullOrWhiteSpace(line) &&
               (line.Contains("Backup complete", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("Backup finished", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("备份已完成", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("备份完成", StringComparison.OrdinalIgnoreCase));
    }

    internal static bool IsBackupFailedLine(string? line)
    {
        return !string.IsNullOrWhiteSpace(line) &&
               (line.Contains("Can't run backup", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("backup is already in progress", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("无法运行此备份", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("无法执行备份", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("备份正在进行中", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("备份已在进行中", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("备份失败", StringComparison.OrdinalIgnoreCase));
    }

    [GeneratedRegex(@"\[(Talk|Chat|Event|Audit)\]|<[^>]+>\s*.+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ChatRegex();
}

