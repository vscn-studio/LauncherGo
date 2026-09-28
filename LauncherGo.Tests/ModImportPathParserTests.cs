using LauncherGo.Ui.Services;
using Xunit;

namespace LauncherGo.Tests;

public sealed class ModImportPathParserTests
{
    [Fact]
    public async Task ParseModImportPaths_TreatsExistingUnquotedPathWithSpacesAsSinglePath()
    {
        var directory = Directory.CreateTempSubdirectory("launchergo-mod-path-");
        try
        {
            var zipPath = Path.Combine(directory.FullName, "Easy Building 1.2.2.zip");
            await File.WriteAllBytesAsync(zipPath, []);

            var paths = ModImportPathParser.Parse(zipPath);

            Assert.Equal([zipPath], paths);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void ParseModImportPaths_ParsesMultipleQuotedPaths()
    {
        var paths = ModImportPathParser.Parse(
            "\"C:\\Mods\\Easy Building.zip\" \"C:\\Mods\\Another Mod.zip\"");

        Assert.Equal(
            ["C:\\Mods\\Easy Building.zip", "C:\\Mods\\Another Mod.zip"],
            paths);
    }

    [Fact]
    public void ParseModImportPaths_TreatsExistingFolderWithSpacesAsSinglePath()
    {
        var directory = Directory.CreateTempSubdirectory("launchergo-mod folder-");
        try
        {
            Assert.Equal([directory.FullName], ModImportPathParser.Parse(directory.FullName));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
