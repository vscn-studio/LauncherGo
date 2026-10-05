using LauncherGo.Services;
using Xunit;

namespace LauncherGo.Tests;

public sealed class EmbeddedModIdentityTests
{
    [Theory]
    [InlineData("launchergoauth", "launchergoauth", "1.1.0", "serverauth.dll", "ServerAuth-1.1.0")]
    [InlineData("launchergoserverbridge", "launchergoserverbridge", "2.2.0", "serverbridge.dll", "LauncherGo Server Bridge-2.2.0")]
    [InlineData("launchergoredirect", "launchergoredirect", "1.2.0", "launchergoredirect.dll", "LauncherGo Gateway Redirect-1.2.0")]
    public void BundledModFolderName_ComesFromValidatedMetadata(
        string sourceFolder, string modId, string version, string dllName, string expectedFolder)
    {
        var sourceRoot = Path.Combine(AppContext.BaseDirectory, "EmbeddedMods", sourceFolder);

        var folderName = EmbeddedModIdentity.GetDeploymentFolderName(sourceRoot, modId, version, dllName);

        Assert.Equal(expectedFolder, folderName);
    }
}
