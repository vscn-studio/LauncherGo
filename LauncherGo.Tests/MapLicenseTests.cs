using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace LauncherGo.Tests;

public sealed class MapLicenseTests
{
    [Theory]
    [InlineData("MapMod.txt")]
    [InlineData("MapWeb.txt")]
    public void LauncherGoMapLicenses_IncludeCompleteProjectLicense(string file)
    {
        Assert.Equal(ReadLicense("LauncherGo.txt"), ReadLicense(file));
    }

    [Theory]
    [InlineData("LiveMapMod.txt")]
    [InlineData("LiveMapWeb.txt")]
    public void LiveMapLicenses_PreserveExactReferencedUpstreamText(string file)
    {
        // MIT license at upstream commit 36cfe158f17b925305162f65fd97142c87c41962.
        const string expectedHash = "24F43BAEFB7CBF8E25B1CF65BE1A65225A236CA4869ED03E3A3C653E027CD622";
        var actualHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ReadLicense(file))));
        Assert.Equal(expectedHash, actualHash);
    }

    [Fact]
    public void SpawnIcon_MatchesReferencedWebCartographerAsset()
    {
        const string expectedHash = "B277BF628419A5584C9804DF67BF42C85FB45998995318CA79F942A1BC53E0AB";
        var icon = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "LicenseFixtures", "spawn.png"));
        Assert.Equal(expectedHash, Convert.ToHexString(SHA256.HashData(icon)));
    }

    private static string ReadLicense(string file) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "LicenseFixtures", file))
            .Replace("\r\n", "\n").TrimEnd() + "\n";
}
