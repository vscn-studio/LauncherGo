using System.IO.Compression;
using LauncherGo.Domains.Enums;
using LauncherGo.Domains.Models;
using LauncherGo.Services;
using Xunit;

namespace LauncherGo.Tests;

public sealed class ModFileArchiveServiceTests
{
    [Fact]
    public async Task UniversalArchive_OnlyIncludesUniversalMods()
    {
        var directory = Directory.CreateTempSubdirectory("launchergo-mod-archive-");
        try
        {
            var serverZip = Path.Combine(directory.FullName, "server.zip");
            await File.WriteAllTextAsync(serverZip, "server");
            var clientZip = Path.Combine(directory.FullName, "client.zip");
            await File.WriteAllTextAsync(clientZip, "client");
            var universalDirectory = Directory.CreateDirectory(Path.Combine(directory.FullName, "universal"));
            await File.WriteAllTextAsync(Path.Combine(universalDirectory.FullName, "modinfo.json"), "{}");
            await File.WriteAllTextAsync(Path.Combine(universalDirectory.FullName, "data.txt"), "universal");
            var config = Directory.CreateDirectory(Path.Combine(directory.FullName, "ModConfig", "nested"));
            await File.WriteAllTextAsync(Path.Combine(config.FullName, "settings.json"), "{}");

            var mods = new ModEntry[]
            {
                CreateMod("server", "Server", serverZip),
                CreateMod("client", "Client", clientZip),
                CreateMod("universal", "Universal", universalDirectory.FullName)
            };
            await using var output = new MemoryStream();
            await new ModFileArchiveService().CreateModArchiveAsync(
                new InstanceProfile { DirectoryPath = directory.FullName },
                mods,
                ModFileArchiveScope.UniversalOnly,
                output);

            output.Position = 0;
            using var archive = new ZipArchive(output, ZipArchiveMode.Read);
            var names = archive.Entries.Select(static entry => entry.FullName).ToList();
            Assert.Contains("Mods/", names);
            Assert.Contains("ModConfig/", names);
            Assert.Contains("ModConfig/nested/settings.json", names);
            Assert.Contains("Mods/universal/modinfo.json", names);
            Assert.Contains("Mods/universal/data.txt", names);
            Assert.DoesNotContain("Mods/server.zip", names);
            Assert.DoesNotContain(names, name => name.Contains("client", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task AllArchive_IncludesEveryModSide()
    {
        var directory = Directory.CreateTempSubdirectory("launchergo-mod-archive-all-");
        try
        {
            var mods = new[]
            {
                CreateFileMod(directory.FullName, "server", "Server"),
                CreateFileMod(directory.FullName, "client", "Client"),
                CreateFileMod(directory.FullName, "universal", "Universal")
            };
            var config = Directory.CreateDirectory(Path.Combine(directory.FullName, "ModConfig"));
            await File.WriteAllTextAsync(Path.Combine(config.FullName, "mod.json"), "{}");
            await using var output = new MemoryStream();

            await new ModFileArchiveService().CreateModArchiveAsync(
                new InstanceProfile { DirectoryPath = directory.FullName },
                mods,
                ModFileArchiveScope.All,
                output);

            output.Position = 0;
            using var archive = new ZipArchive(output, ZipArchiveMode.Read);
            var names = archive.Entries.Select(static entry => entry.FullName).ToList();
            Assert.Contains("Mods/", names);
            Assert.Contains("ModConfig/", names);
            Assert.Contains("ModConfig/mod.json", names);
            Assert.Contains("Mods/server.zip", names);
            Assert.Contains("Mods/client.zip", names);
            Assert.Contains("Mods/universal.zip", names);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static ModEntry CreateFileMod(string directoryPath, string id, string side)
    {
        var path = Path.Combine(directoryPath, $"{id}.zip");
        File.WriteAllText(path, id);
        return CreateMod(id, side, path);
    }

    private static ModEntry CreateMod(string id, string side, string filePath) => new()
    {
        Name = id,
        ModId = id,
        Version = "1.0.0",
        Side = side,
        FilePath = filePath,
        Status = "OK"
    };
}
