using System.Text.Json;
using LauncherGo.Abstractions.Services;
using LauncherGo.Domains.Models;

namespace LauncherGo.Services;

/// <summary>
///     自动化设置持久化服务
/// </summary>
public class AutomationSettingsService : IAutomationSettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private static string LegacySettingsPath => Path.Combine(WorkspacePathHelper.WorkspaceRoot, "automation-settings.json");

    private static string SettingsRoot => Path.Combine(WorkspacePathHelper.WorkspaceRoot, "automation");

    public async Task<AutomationSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        WorkspacePathHelper.EnsureWorkspace();
        if (!File.Exists(LegacySettingsPath))
            return BuildDefaults();

        try
        {
            var json = await File.ReadAllTextAsync(LegacySettingsPath, cancellationToken);
            var parsed = JsonSerializer.Deserialize<AutomationSettings>(json, JsonOptions) ?? BuildDefaults();
            return Normalize(parsed);
        }
        catch
        {
            return BuildDefaults();
        }
    }

    public async Task<AutomationSettings> LoadAsync(InstanceProfile profile, CancellationToken cancellationToken = default)
    {
        WorkspacePathHelper.EnsureWorkspace();
        var settingsPath = GetSettingsPath(profile);
        if (!File.Exists(settingsPath))
        {
            var defaults = BuildDefaults();
            defaults.TargetProfileId = profile.Id;
            if (File.Exists(LegacySettingsPath))
            {
                var legacy = await LoadAsync(cancellationToken);
                if (string.IsNullOrWhiteSpace(legacy.TargetProfileId) ||
                    legacy.TargetProfileId.Equals(profile.Id, StringComparison.OrdinalIgnoreCase))
                {
                    legacy.TargetProfileId = profile.Id;
                    return Normalize(legacy);
                }
            }

            return Normalize(defaults);
        }

        try
        {
            var json = await File.ReadAllTextAsync(settingsPath, cancellationToken);
            var parsed = JsonSerializer.Deserialize<AutomationSettings>(json, JsonOptions) ?? BuildDefaults();
            parsed.TargetProfileId = profile.Id;
            return Normalize(parsed);
        }
        catch
        {
            var fallback = BuildDefaults();
            fallback.TargetProfileId = profile.Id;
            return Normalize(fallback);
        }
    }

    public async Task<IReadOnlyList<AutomationSettings>> LoadAllAsync(
        IReadOnlyList<InstanceProfile> profiles,
        CancellationToken cancellationToken = default)
    {
        var settings = new List<AutomationSettings>();
        foreach (var profile in profiles)
        {
            settings.Add(await LoadAsync(profile, cancellationToken));
        }

        return settings;
    }

    public async Task SaveAsync(AutomationSettings settings, CancellationToken cancellationToken = default)
    {
        WorkspacePathHelper.EnsureWorkspace();
        var normalized = Normalize(settings);
        var json = JsonSerializer.Serialize(normalized, JsonOptions);
        await File.WriteAllTextAsync(LegacySettingsPath, json, cancellationToken);
    }

    public async Task SaveAsync(InstanceProfile profile, AutomationSettings settings, CancellationToken cancellationToken = default)
    {
        WorkspacePathHelper.EnsureWorkspace();
        Directory.CreateDirectory(SettingsRoot);
        settings.TargetProfileId = profile.Id;
        var normalized = Normalize(settings);
        var json = JsonSerializer.Serialize(normalized, JsonOptions);
        await File.WriteAllTextAsync(GetSettingsPath(profile), json, cancellationToken);
    }

    public string GetSettingsPath(InstanceProfile profile)
    {
        WorkspacePathHelper.EnsureWorkspace();
        Directory.CreateDirectory(SettingsRoot);
        var id = string.IsNullOrWhiteSpace(profile.Id) ? "default" : profile.Id.Trim();
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            id = id.Replace(invalid, '_');
        }

        return Path.Combine(SettingsRoot, $"{id}.json");
    }

    private static AutomationSettings BuildDefaults()
    {
        return new AutomationSettings
        {
            RestartSchedulerEnabled = false,
            BackupEnabled = false,
            BroadcastEnabled = false,
            CommandEnabled = false,
            ExportLogEnabled = false,
            BackupBeforeShutdown = true,
            ExportBeforeShutdown = true,
            ExportIncludeChat = true,
            ExportIncludeServerInfo = true,
            ClearCacheBeforeStart = false,
            AutomationScriptsEnabled = false,
            AutomationScripts = [],
            ActionWindows =
            [
                new AutomationActionWindow
                {
                    ScheduleMode = AutomationScheduleMode.Weekly,
                    StartDayOfWeek = 1,
                    EndDayOfWeek = 5,
                    StartTime = "08:00",
                    EndTime = "23:00",
                    Action = AutomationActionType.Start,
                    Enabled = true
                },
                new AutomationActionWindow
                {
                    ScheduleMode = AutomationScheduleMode.Weekly,
                    StartDayOfWeek = 6,
                    EndDayOfWeek = 7,
                    StartTime = "00:00",
                    EndTime = "23:59",
                    Action = AutomationActionType.Stop,
                    Enabled = true
                }
            ],
            TimeWindows =
            [
                new DailyTimeWindow
                {
                    StartTime = "08:00",
                    EndTime = "23:00",
                    Enabled = true
                }
            ],
            BackupSchedules =
            [
                new BackupSchedule
                {
                    Type = BackupScheduleType.Daily,
                    Time = "03:00",
                    AnchorDate = DateTime.Now.ToString("yyyy-MM-dd")
                }
            ],
            BroadcastMessages =
            [
                new ScheduledBroadcastMessage
                {
                    Time = "12:00",
                    Message = "服务器例行播报",
                    Enabled = false
                }
            ],
            ScheduledCommands =
            [
                new ScheduledServerCommand
                {
                    Time = "12:00",
                    Command = "/time",
                    Enabled = false
                }
            ],
            ExportTimes = ["05:00"]
        };
    }

    internal static AutomationSettings Normalize(AutomationSettings settings)
    {
        var normalizedWindows = (settings.TimeWindows ?? [])
            .Where(window => window is not null)
            .Select(window => new DailyTimeWindow
            {
                StartTime = NormalizeTime(window.StartTime, "08:00"),
                EndTime = NormalizeTime(window.EndTime, "23:00"),
                Enabled = window.Enabled
            })
            .ToList();
        if (normalizedWindows.Count == 0)
        {
            normalizedWindows.Add(new DailyTimeWindow
            {
                StartTime = "08:00",
                EndTime = "23:00",
                Enabled = true
            });
        }

        var normalizedActionWindows = NormalizeActionWindows(settings.ActionWindows);
        if (normalizedActionWindows.Count == 0)
        {
            normalizedActionWindows = MigrateLegacyTimeWindows(normalizedWindows);
        }

        var normalizedMessages = (settings.BroadcastMessages ?? [])
            .Where(item => item is not null)
            .Select(item => new ScheduledBroadcastMessage
            {
                Time = NormalizeTime(item.Time, "12:00"),
                Message = item.Message?.Trim() ?? string.Empty,
                Enabled = item.Enabled
            })
            .Where(item => !string.IsNullOrWhiteSpace(item.Message))
            .ToList();

        var normalizedCommands = (settings.ScheduledCommands ?? [])
            .Where(item => item is not null)
            .Select(item =>
            {
                var sourceSchedule = item.Schedule ?? new BackupSchedule
                {
                    Enabled = item.Enabled,
                    Type = BackupScheduleType.Daily,
                    Time = item.Time,
                    AnchorDate = DateTime.Now.ToString("yyyy-MM-dd")
                };
                var schedule = BackupScheduleCalculator.Normalize(sourceSchedule, DateTime.Now);
                return new ScheduledServerCommand
                {
                    Time = schedule.Type == BackupScheduleType.Daily ? schedule.Time : NormalizeTime(item.Time, "12:00"),
                    Schedule = schedule,
                    Command = item.Command?.Trim() ?? string.Empty,
                    Enabled = item.Enabled
                };
            })
            .Where(item => !string.IsNullOrWhiteSpace(item.Command))
            .ToList();

        var backupSources = settings.BackupSchedules ?? [];
        if (backupSources.Count == 0 && settings.BackupTimes is { Count: > 0 })
        {
            backupSources = settings.BackupTimes
                .Select(time => new BackupSchedule
                {
                    Type = BackupScheduleType.Daily,
                    Time = time,
                    AnchorDate = DateTime.Now.ToString("yyyy-MM-dd")
                })
                .ToList();
        }

        var normalizedBackupSchedules = backupSources
            .Where(schedule => schedule is not null)
            .Select(schedule => BackupScheduleCalculator.Normalize(schedule, DateTime.Now))
            .GroupBy(GetBackupScheduleKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(schedule => schedule.Type)
            .ThenBy(schedule => schedule.Time, StringComparer.OrdinalIgnoreCase)
            .ThenBy(schedule => schedule.Interval)
            .ToList();

        var normalizedExportTimes = (settings.ExportTimes ?? [])
            .Select(time => NormalizeTime(time, string.Empty))
            .Where(time => !string.IsNullOrWhiteSpace(time))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var normalizedScripts = (settings.AutomationScripts ?? [])
            .Where(script => script is not null)
            .Select(script => new AutomationScript
            {
                Enabled = script.Enabled,
                Trigger = Enum.IsDefined(script.Trigger)
                    ? script.Trigger
                    : AutomationScriptTrigger.BeforeStart,
                ScriptPath = script.ScriptPath?.Trim() ?? string.Empty
            })
            .Where(script => !string.IsNullOrWhiteSpace(script.ScriptPath))
            .Where(script => script.ScriptPath.EndsWith(".bat", StringComparison.OrdinalIgnoreCase) ||
                             script.ScriptPath.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase))
            .GroupBy(script => $"{script.Trigger}|{script.ScriptPath}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();

        return new AutomationSettings
        {
            TargetProfileId = settings.TargetProfileId?.Trim() ?? string.Empty,
            RestartSchedulerEnabled = settings.RestartSchedulerEnabled,
            BackupEnabled = settings.BackupEnabled,
            TimeWindows = normalizedWindows,
            ActionWindows = normalizedActionWindows,
            BackupSchedules = normalizedBackupSchedules,
            BackupRetentionCount = Math.Clamp(settings.BackupRetentionCount, 0, 100_000),
            BackupTimes = [],
            BackupBeforeShutdown = settings.BackupBeforeShutdown,
            BroadcastEnabled = settings.BroadcastEnabled,
            BroadcastMessages = normalizedMessages,
            CommandEnabled = settings.CommandEnabled,
            ScheduledCommands = normalizedCommands,
            ExportLogEnabled = settings.ExportLogEnabled,
            ExportTimes = normalizedExportTimes,
            ExportBeforeShutdown = settings.ExportBeforeShutdown,
            ExportIncludeChat = settings.ExportIncludeChat,
            ExportIncludeServerInfo = settings.ExportIncludeServerInfo,
            ClearCacheBeforeStart = settings.ClearCacheBeforeStart,
            AutomationScriptsEnabled = settings.AutomationScriptsEnabled,
            AutomationScripts = normalizedScripts
        };
    }

    private static string GetBackupScheduleKey(BackupSchedule schedule)
    {
        return string.Join(
            '|',
            schedule.Enabled,
            schedule.Type,
            schedule.DayOfMonth,
            schedule.DayOfWeek,
            schedule.Time,
            schedule.MinuteOfHour,
            schedule.Interval,
            schedule.AnchorDate);
    }

    private static string NormalizeTime(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
            return fallback;

        return TimeSpan.TryParse(value.Trim(), out var parsed)
            ? $"{parsed.Hours:00}:{parsed.Minutes:00}"
            : fallback;
    }

    private static List<AutomationActionWindow> NormalizeActionWindows(IReadOnlyList<AutomationActionWindow>? windows)
    {
        var normalized = (windows ?? [])
            .Where(window => window is not null)
            .Select(window => new AutomationActionWindow
            {
                ScheduleMode = window.ScheduleMode,
                StartDayOfWeek = NormalizeWeekDay(window.StartDayOfWeek, 1),
                EndDayOfWeek = NormalizeWeekDay(window.EndDayOfWeek, 7),
                StartDate = NormalizeDate(window.StartDate),
                EndDate = NormalizeDate(window.EndDate),
                StartTime = NormalizeTime(window.StartTime, "08:00"),
                EndTime = NormalizeTime(window.EndTime, "23:00"),
                Action = window.Action,
                Enabled = window.Enabled
            })
            .ToList();

        return normalized;
    }

    private static List<AutomationActionWindow> MigrateLegacyTimeWindows(IReadOnlyList<DailyTimeWindow> windows)
    {
        var migrated = new List<AutomationActionWindow>();
        foreach (var window in windows)
        {
            migrated.Add(new AutomationActionWindow
            {
                ScheduleMode = AutomationScheduleMode.Weekly,
                StartDayOfWeek = 1,
                EndDayOfWeek = 7,
                StartTime = NormalizeTime(window.StartTime, "08:00"),
                EndTime = NormalizeTime(window.EndTime, "23:00"),
                Action = AutomationActionType.Start,
                Enabled = window.Enabled
            });
        }

        return migrated;
    }

    private static int NormalizeWeekDay(int value, int fallback)
    {
        return value is >= 1 and <= 7 ? value : fallback;
    }

    private static string NormalizeDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return DateOnly.TryParse(value.Trim(), out var parsed)
            ? parsed.ToString("yyyy-MM-dd")
            : string.Empty;
    }
}

