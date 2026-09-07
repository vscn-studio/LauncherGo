using LauncherGo.Domains.Enums;
using LauncherGo.Domains.Models;
using LauncherGo.Services;
using Xunit;

namespace LauncherGo.Tests;

public sealed class LauncherUpdateServiceTests
{
    [Theory]
    [InlineData("v2.5.4", "2.5.4")]
    [InlineData("v2.6.8-pre.1", "2.6.8-pre.1")]
    [InlineData("2.5.4-preview.2+abc", "2.5.4-preview.2")]
    public void NormalizeVersion_PreservesPrereleaseAndRemovesTagAndMetadata(string input, string expected)
    {
        Assert.Equal(expected, LauncherUpdateService.NormalizeVersion(input));
    }

    [Fact]
    public void SelectAsset_UsesCurrentPackagePrefix()
    {
        var assets = new[]
        {
            new LauncherUpdateAsset { Name = "LauncherGo-Setup-2.0.0-win-x64.exe" },
            new LauncherUpdateAsset { Name = "LauncherGo-Small-Setup-2.0.0-win-x64.exe" },
            new LauncherUpdateAsset { Name = "LauncherGo-portable-2.0.0-win-x64.zip" },
            new LauncherUpdateAsset { Name = "LauncherGo-small-package-2.0.0-win-x64.zip" }
        };

        Assert.Equal("LauncherGo-Setup-2.0.0-win-x64.exe", LauncherUpdateService.SelectAsset(assets, LauncherPackageKind.Installer)?.Name);
        Assert.Equal("LauncherGo-Small-Setup-2.0.0-win-x64.exe", LauncherUpdateService.SelectAsset(assets, LauncherPackageKind.SmallInstaller)?.Name);
        Assert.Equal("LauncherGo-portable-2.0.0-win-x64.zip", LauncherUpdateService.SelectAsset(assets, LauncherPackageKind.Portable)?.Name);
        Assert.Equal("LauncherGo-small-package-2.0.0-win-x64.zip", LauncherUpdateService.SelectAsset(assets, LauncherPackageKind.SmallPackage)?.Name);
    }

    [Fact]
    public void BuildProxyUrl_PrependsSelectedProxy()
    {
        const string url = "https://api.github.com/repos/vscn-studio/LauncherGo/releases/latest";
        Assert.Equal(url, LauncherUpdateService.BuildProxyUrl(url, GitHubProxyKind.Direct));
        Assert.Equal("https://gh-proxy.com/" + url, LauncherUpdateService.BuildProxyUrl(url, GitHubProxyKind.GhProxy));
    }

    [Theory]
    [InlineData("2.10.0", "2.9.9", 1)]
    [InlineData("2.5.4", "2.5.4", 0)]
    [InlineData("2.6.8", "2.6.8-pre.1", 1)]
    [InlineData("2.6.8-pre.2", "2.6.8-pre.1", 1)]
    [InlineData("2.6.8-pre.1", "2.6.8-pre.1+build.9", 0)]
    [InlineData("2.6.8-alpha", "2.6.8-alpha.1", -1)]
    [InlineData("2.6.8-beta", "2.6.8-alpha", 1)]
    public void CompareVersions_UsesSemanticVersionPrecedence(string left, string right, int expectedSign)
    {
        Assert.Equal(expectedSign, Math.Sign(LauncherUpdateService.CompareVersions(left, right)));
    }

    [Fact]
    public void SelectNewestRelease_IncludesPrereleaseButSkipsDrafts()
    {
        var tag = LauncherUpdateService.SelectNewestReleaseTag(
        [
            new LauncherReleaseCandidate
            {
                TagName = "v2.6.6",
                PublishedAtUtc = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero)
            },
            new LauncherReleaseCandidate
            {
                TagName = "v2.6.7-preview.1",
                IsPrerelease = true,
                PublishedAtUtc = new DateTimeOffset(2026, 9, 2, 0, 0, 0, TimeSpan.Zero)
            },
            new LauncherReleaseCandidate
            {
                TagName = "v9.0.0-draft",
                IsDraft = true,
                PublishedAtUtc = new DateTimeOffset(2026, 9, 3, 0, 0, 0, TimeSpan.Zero)
            }
        ]);

        Assert.Equal("v2.6.7-preview.1", tag);
    }

    [Fact]
    public void CleanupUpdateCache_RemovesOlderDirectories()
    {
        var updateRoot = Path.Combine(Path.GetTempPath(), $"launchergo-update-cleanup-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(updateRoot);
            for (var index = 0; index < 3; index++)
            {
                var directory = Path.Combine(updateRoot, $"2.6.{index}");
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, "asset.exe"), index.ToString());
                Directory.SetLastWriteTimeUtc(directory, DateTime.UtcNow.AddMinutes(-index));
            }

            var removed = LauncherUpdateService.CleanupUpdateCache(updateRoot, retainCount: 1);

            Assert.Equal(2, removed);
            Assert.True(Directory.Exists(Path.Combine(updateRoot, "2.6.0")));
            Assert.False(Directory.Exists(Path.Combine(updateRoot, "2.6.1")));
            Assert.False(Directory.Exists(Path.Combine(updateRoot, "2.6.2")));
        }
        finally
        {
            if (Directory.Exists(updateRoot))
                Directory.Delete(updateRoot, recursive: true);
        }
    }

}
