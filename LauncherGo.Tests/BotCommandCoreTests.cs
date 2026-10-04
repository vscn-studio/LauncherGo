using LauncherGo.Services;
using Xunit;

namespace LauncherGo.Tests;

public sealed class BotCommandCoreTests
{
    [Theory]
    [InlineData("/server status", "server", "status")]
    [InlineData("  /HELP  ", "help", "")]
    [InlineData("send say hi", "send", "say hi")]
    public void Parse_NormalizesSlashCommand(string raw, string expectedCommand, string expectedArguments)
    {
        var result = RobotCommandDispatcher.Parse(raw);
        Assert.Equal(expectedCommand, result.Command);
        Assert.Equal(expectedArguments, result.Arguments);
    }

    [Fact]
    public void SplitText_RespectsMessageLimit()
    {
        var parts = RobotCommandDispatcher.SplitText(new string('x', 4500), 2000);
        Assert.Equal(3, parts.Count);
        Assert.All(parts, part => Assert.InRange(part.Length, 1, 2000));
    }
}
