namespace LauncherGo.Domains.Models;

/// <summary>
///     定时服务器命令
/// </summary>
public class ScheduledServerCommand
{
    /// <summary>
    ///     执行时间，格式 HH:mm
    /// </summary>
    public string Time { get; set; } = "12:00";

    /// <summary>
    ///     命令执行周期。旧配置仅有 Time 时会迁移为每日计划。
    /// </summary>
    public BackupSchedule? Schedule { get; set; }

    /// <summary>
    ///     服务器命令
    /// </summary>
    public string Command { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;
}
