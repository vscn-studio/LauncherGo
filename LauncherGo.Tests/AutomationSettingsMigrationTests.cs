using LauncherGo.Domains.Models;
using LauncherGo.Services;
using Xunit;

namespace LauncherGo.Tests;

public sealed class AutomationSettingsMigrationTests
{
    [Fact]
    public void LegacyBackupTimesMigrateToDailySchedules()
    {
        var settings = AutomationSettingsService.Normalize(new AutomationSettings
        {
            BackupEnabled = true,
            BackupTimes = ["01:00", "13:30", "13:30"]
        });

        Assert.Empty(settings.BackupTimes);
        Assert.Collection(
            settings.BackupSchedules,
            first =>
            {
                Assert.Equal(BackupScheduleType.Daily, first.Type);
                Assert.Equal("01:00", first.Time);
            },
            second =>
            {
                Assert.Equal(BackupScheduleType.Daily, second.Type);
                Assert.Equal("13:30", second.Time);
            });
    }

    [Fact]
    public void BackupScheduleValuesAreNormalized()
    {
        var settings = AutomationSettingsService.Normalize(new AutomationSettings
        {
            BackupRetentionCount = -4,
            BackupSchedules =
            [
                new BackupSchedule
                {
                    Type = BackupScheduleType.Hourly,
                    MinuteOfHour = 72,
                    Interval = 0,
                    Time = "invalid"
                }
            ]
        });

        var schedule = Assert.Single(settings.BackupSchedules);
        Assert.Equal(0, settings.BackupRetentionCount);
        Assert.Equal(59, schedule.MinuteOfHour);
        Assert.Equal(1, schedule.Interval);
        Assert.Equal("03:00", schedule.Time);
        Assert.False(string.IsNullOrWhiteSpace(schedule.Id));
        Assert.False(string.IsNullOrWhiteSpace(schedule.AnchorDate));
    }

    [Fact]
    public void LegacyScheduledCommandTimeMigratesToDailySchedule()
    {
        var settings = AutomationSettingsService.Normalize(new AutomationSettings
        {
            CommandEnabled = true,
            ScheduledCommands =
            [
                new ScheduledServerCommand
                {
                    Time = "21:30",
                    Command = "/time night"
                }
            ]
        });

        var command = Assert.Single(settings.ScheduledCommands);
        Assert.NotNull(command.Schedule);
        Assert.Equal(BackupScheduleType.Daily, command.Schedule!.Type);
        Assert.Equal("21:30", command.Schedule.Time);
        Assert.Equal("21:30", command.Time);
    }

    [Fact]
    public void ScheduledCommandKeepsNonDailySchedule()
    {
        var settings = AutomationSettingsService.Normalize(new AutomationSettings
        {
            ScheduledCommands =
            [
                new ScheduledServerCommand
                {
                    Command = "/server save",
                    Schedule = new BackupSchedule
                    {
                        Type = BackupScheduleType.EveryNHours,
                        Interval = 6,
                        Time = "02:00",
                        AnchorDate = "2026-08-11"
                    }
                }
            ]
        });

        var command = Assert.Single(settings.ScheduledCommands);
        Assert.Equal(BackupScheduleType.EveryNHours, command.Schedule!.Type);
        Assert.Equal(6, command.Schedule.Interval);
        Assert.True(BackupScheduleCalculator.IsDue(command.Schedule, new DateTime(2026, 8, 11, 8, 0, 0)));
    }

    [Fact]
    public void AutomationScriptsKeepSupportedTriggersAndDropInvalidFiles()
    {
        var settings = AutomationSettingsService.Normalize(new AutomationSettings
        {
            AutomationScriptsEnabled = true,
            AutomationScripts =
            [
                new AutomationScript
                {
                    Trigger = AutomationScriptTrigger.BeforeStart,
                    ScriptPath = "  C:\\scripts\\clear.bat  "
                },
                new AutomationScript
                {
                    Trigger = AutomationScriptTrigger.BeforeStart,
                    ScriptPath = "C:\\scripts\\clear.bat"
                },
                new AutomationScript
                {
                    Trigger = (AutomationScriptTrigger)999,
                    ScriptPath = "C:\\scripts\\stop.cmd"
                },
                new AutomationScript
                {
                    ScriptPath = "C:\\scripts\\readme.txt"
                }
            ]
        });

        Assert.True(settings.AutomationScriptsEnabled);
        Assert.Equal(2, settings.AutomationScripts.Count);
        Assert.Contains(settings.AutomationScripts, script =>
            script.Trigger == AutomationScriptTrigger.BeforeStart &&
            script.ScriptPath == "C:\\scripts\\clear.bat");
        Assert.Contains(settings.AutomationScripts, script =>
            script.Trigger == AutomationScriptTrigger.BeforeStart &&
            script.ScriptPath == "C:\\scripts\\stop.cmd");
    }

    [Fact]
    public void ClearProfileCacheOnlyRemovesCacheContents()
    {
        using var root = new TempDirectory();
        var profilePath = Directory.CreateDirectory(Path.Combine(root.Path, "profile")).FullName;
        Directory.CreateDirectory(Path.Combine(profilePath, "Mods"));
        Directory.CreateDirectory(Path.Combine(profilePath, "Cache", "nested"));
        File.WriteAllText(Path.Combine(profilePath, "Cache", "stale.txt"), "stale");
        File.WriteAllText(Path.Combine(profilePath, "Cache", "nested", "stale.bin"), "stale");
        File.WriteAllText(Path.Combine(profilePath, "Mods", "keep.txt"), "keep");

        AutomationLifecycleService.ClearProfileCache(new InstanceProfile { DirectoryPath = profilePath });

        Assert.True(Directory.Exists(Path.Combine(profilePath, "Cache")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(profilePath, "Cache")));
        Assert.True(File.Exists(Path.Combine(profilePath, "Mods", "keep.txt")));
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "LauncherGo.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
                // Best effort cleanup for test artifacts.
            }
        }
    }
}
