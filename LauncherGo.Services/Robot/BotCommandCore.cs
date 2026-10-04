namespace LauncherGo.Services;

public sealed record BotAttachment(string FilePath, string FileName, string ContentType = "application/octet-stream");

public sealed record BotMessage(string Text = "", IReadOnlyList<BotAttachment>? Attachments = null, bool Ephemeral = false);

public sealed record BotPermissionContext(
    string UserId,
    IReadOnlySet<string>? RoleIds = null,
    bool IsAdministrator = false);

public sealed record BotCommandContext(
    string Command,
    string Arguments,
    string? ProfileId,
    string GuildId,
    string ChannelId,
    BotPermissionContext Permission);

public interface IBotMessageAdapter
{
    Task SendAsync(BotMessage message, CancellationToken cancellationToken = default);
}

/// <summary>Shared command parsing helpers used by the OneBot adapter.</summary>
public sealed class RobotCommandDispatcher
{
    public static IReadOnlyList<string> SplitText(string? text, int maxLength = 2000)
    {
        if (string.IsNullOrEmpty(text)) return [string.Empty];
        maxLength = Math.Max(1, maxLength);
        var result = new List<string>();
        for (var offset = 0; offset < text.Length; offset += maxLength)
            result.Add(text.Substring(offset, Math.Min(maxLength, text.Length - offset)));
        return result;
    }

    public static (string Command, string Arguments) Parse(string? raw)
    {
        var value = (raw ?? string.Empty).Trim();
        if (value.StartsWith('/')) value = value[1..];
        var separator = value.IndexOf(' ');
        return separator < 0
            ? (value.ToLowerInvariant(), string.Empty)
            : (value[..separator].ToLowerInvariant(), value[(separator + 1)..].Trim());
    }
}
