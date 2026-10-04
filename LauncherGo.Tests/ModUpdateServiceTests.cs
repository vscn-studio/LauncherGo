using System.Text.Json;
using LauncherGo.Services;
using Xunit;

namespace LauncherGo.Tests;

public sealed class ModUpdateServiceTests
{
    [Theory]
    [InlineData("1.0.0", "1.1.0", true)]
    [InlineData("v1.2.0", "1.2.0", false)]
    [InlineData("1.2.0-beta.2", "1.2.0-beta.10", true)]
    [InlineData("1.2.0", "1.2.0-beta.1", false)]
    [InlineData("unknown", "unknown", false)]
    public void CompareVersionsDetectsSemanticUpdates(string current, string latest, bool expectedUpdate)
    {
        var comparison = ModUpdateService.CompareVersions(current, latest);

        Assert.Equal(expectedUpdate, comparison < 0);
    }

    [Fact]
    public void SelectLatestReleaseUsesVersionBeforeCreatedTime()
    {
        using var document = JsonDocument.Parse("""
            [
              { "modversion": "1.19.8", "created": "2026-10-04T12:00:00Z" },
              { "modversion": "2.0.0", "created": "2026-10-01T12:00:00Z" },
              { "modversion": "1.20.0", "created": "2026-10-03T12:00:00Z" }
            ]
            """);

        var selected = ModUpdateService.SelectLatestRelease(document.RootElement.EnumerateArray());

        Assert.Equal("2.0.0", selected.GetProperty("modversion").GetString());
    }

    [Fact]
    public void SelectLatestReleaseUsesNewestCreatedTimeForDuplicateVersions()
    {
        using var document = JsonDocument.Parse("""
            [
              { "modversion": "2.0.0", "created": "2026-10-01T12:00:00Z" },
              { "modversion": "2.0.0", "created": "2026-10-04T12:00:00Z" }
            ]
            """);

        var selected = ModUpdateService.SelectLatestRelease(document.RootElement.EnumerateArray());

        Assert.Equal("2026-10-04T12:00:00Z", selected.GetProperty("created").GetString());
    }
}
