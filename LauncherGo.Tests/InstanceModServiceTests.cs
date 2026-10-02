using System.IO.Compression;
using LauncherGo.Abstractions.Services;
using LauncherGo.Domains.Models;
using LauncherGo.Services;
using Xunit;

namespace LauncherGo.Tests;

public sealed class InstanceModServiceTests
{
    [Fact]
    public async Task GetModsAsync_DoesNotTreatInvalidFolderNameAsModId()
    {
        var directory = Directory.CreateTempSubdirectory("launchergo-mod-invalid-");
        try
        {
            var modsPath = Directory.CreateDirectory(Path.Combine(directory.FullName, "Mods"));
            var authPath = Directory.CreateDirectory(Path.Combine(modsPath.FullName, "ServerAuth-1.1.0"));
            await File.WriteAllTextAsync(Path.Combine(authPath.FullName, "modinfo.json"),
                """{"name":"ServerAuth","version":"1.1.0"}""");
            Directory.CreateDirectory(Path.Combine(modsPath.FullName, "Other-1.0"));

            var service = new InstanceModService(new StubServerConfigService());
            var mods = await service.GetModsAsync(new InstanceProfile { DirectoryPath = directory.FullName });

            Assert.Equal(2, mods.Count);
            Assert.All(mods, mod =>
            {
                Assert.Equal(string.Empty, mod.ModId);
                Assert.Equal("InvalidMetadata", mod.Status);
                Assert.False(mod.IsDuplicate);
            });
            Assert.Contains(mods, mod => mod.Name == "ServerAuth-1.1.0");
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task GetModsAsync_ReadsPascalCaseModMetadataFields()
    {
        var directory = Directory.CreateTempSubdirectory("launchergo-mod-metadata-");
        try
        {
            var modDirectory = Directory.CreateDirectory(Path.Combine(directory.FullName, "Mods", "clayworks"));
            await File.WriteAllTextAsync(
                Path.Combine(modDirectory.FullName, "modinfo.json"),
                """
                {
                  "Type": "content",
                  "TextureSize": 32,
                  "Name": "Clayworks",
                  "Version": "0.6.0",
                  "Side": "Server",
                  "NetworkVersion": null,
                  "ModID": "clayworks",
                  "Dependencies": [
                    { "ModID": "game", "Version": "1.20.0" }
                  ]
                }
                """);

            var service = new InstanceModService(new StubServerConfigService());
            var mods = await service.GetModsAsync(new InstanceProfile { DirectoryPath = directory.FullName });

            var mod = Assert.Single(mods);
            Assert.Equal("Clayworks", mod.Name);
            Assert.Equal("clayworks", mod.ModId);
            Assert.Equal("0.6.0", mod.Version);
            Assert.Equal("Server", mod.Side);
            var dependency = Assert.Single(mod.Dependencies);
            Assert.Equal("game", dependency.ModId);
            Assert.Equal("1.20.0", dependency.Version);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task GetModsAsync_AcceptsTrailingCommasAndCommentsInModInfo()
    {
        var directory = Directory.CreateTempSubdirectory("launchergo-mod-lenient-metadata-");
        try
        {
            var modDirectory = Directory.CreateDirectory(Path.Combine(directory.FullName, "Mods", "legacy-mod"));
            await File.WriteAllTextAsync(
                Path.Combine(modDirectory.FullName, "modinfo.json"),
                """
                {
                  // Older mods sometimes leave a comma after the final field.
                  "modid": "legacy-mod",
                  "name": "Legacy Mod",
                  "version": 3.1,
                }
                """);

            var service = new InstanceModService(new StubServerConfigService());
            var mods = await service.GetModsAsync(new InstanceProfile { DirectoryPath = directory.FullName });

            var mod = Assert.Single(mods);
            Assert.Equal("legacy-mod", mod.ModId);
            Assert.Equal("Legacy Mod", mod.Name);
            Assert.Equal("3.1", mod.Version);
            Assert.Equal("OK", mod.Status);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ImportModsAsync_ImportsZipFilesAndModDirectories()
    {
        var directory = Directory.CreateTempSubdirectory("launchergo-mod-import-");
        try
        {
            var source = Directory.CreateDirectory(Path.Combine(directory.FullName, "incoming"));
            var modFolder = Directory.CreateDirectory(Path.Combine(source.FullName, "folder-mod"));
            await File.WriteAllTextAsync(
                Path.Combine(modFolder.FullName, "modinfo.json"),
                "{\"modid\":\"foldermod\",\"version\":\"1.0.0\"}");
            var assets = Directory.CreateDirectory(Path.Combine(modFolder.FullName, "assets"));
            await File.WriteAllTextAsync(Path.Combine(assets.FullName, "content.json"), "{}");

            var zipPath = Path.Combine(source.FullName, "zip-mod.zip");
            using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                var entry = archive.CreateEntry("modinfo.json");
                await using var writer = new StreamWriter(entry.Open());
                await writer.WriteAsync("{\"modid\":\"zipmod\",\"version\":\"2.0.0\"}");
            }

            await File.WriteAllTextAsync(Path.Combine(source.FullName, "not-a-mod.zip"), "not a zip");

            var service = new InstanceModService(new StubServerConfigService());
            var imported = await service.ImportModsAsync(
                new InstanceProfile { DirectoryPath = directory.FullName },
                [modFolder.FullName, zipPath]);

            Assert.Equal(2, imported.Count);
            Assert.Contains(imported, mod => mod.ModId == "foldermod");
            Assert.Contains(imported, mod => mod.ModId == "zipmod");
            Assert.True(File.Exists(Path.Combine(directory.FullName, "Mods", "zipmod-2.0.0.zip")));
            Assert.True(Directory.Exists(Path.Combine(directory.FullName, "Mods", "foldermod-1.0.0")));
            Assert.True(File.Exists(Path.Combine(directory.FullName, "Mods", "foldermod-1.0.0", "assets", "content.json")));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ImportModsAsync_ReplacesMatchingModButRejectsDifferentModWithSameName()
    {
        var directory = Directory.CreateTempSubdirectory("launchergo-mod-replace-");
        try
        {
            var incoming = Directory.CreateDirectory(Path.Combine(directory.FullName, "incoming"));
            var profile = new InstanceProfile { DirectoryPath = directory.FullName };
            var service = new InstanceModService(new StubServerConfigService());
            var first = Path.Combine(incoming.FullName, "first.zip");
            var replacement = Path.Combine(incoming.FullName, "replacement.zip");
            var conflict = Path.Combine(incoming.FullName, "conflict.zip");
            CreateModZip(first, "same", "Shared Name", "1.0", "first");
            CreateModZip(replacement, "same", "Shared Name", "1.0", "replacement");
            CreateModZip(conflict, "other", "Shared Name", "1.0", "other");

            await service.ImportModsAsync(profile, [first]);
            await service.ImportModsAsync(profile, [replacement]);

            var installedPath = Path.Combine(directory.FullName, "Mods", "Shared Name-1.0.zip");
            using (var archive = ZipFile.OpenRead(installedPath))
            using (var reader = new StreamReader(archive.GetEntry("marker.txt")!.Open()))
                Assert.Equal("replacement", await reader.ReadToEndAsync());

            await Assert.ThrowsAsync<InvalidOperationException>(() => service.ImportModsAsync(profile, [conflict]));
            var installed = Assert.Single(await service.GetModsAsync(profile));
            Assert.Equal("same", installed.ModId);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static void CreateModZip(string path, string modId, string name, string version, string marker)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        using (var writer = new StreamWriter(archive.CreateEntry("modinfo.json").Open()))
            writer.Write(System.Text.Json.JsonSerializer.Serialize(new { modid = modId, name, version }));
        using (var writer = new StreamWriter(archive.CreateEntry("marker.txt").Open()))
            writer.Write(marker);
    }

    [Fact]
    public async Task NormalizeModNamesAsync_RenamesZipAndFolderFromMetadata()
    {
        var directory = Directory.CreateTempSubdirectory("launchergo-mod-rename-");
        try
        {
            var mods = Directory.CreateDirectory(Path.Combine(directory.FullName, "Mods"));
            var folder = Directory.CreateDirectory(Path.Combine(mods.FullName, "old-folder"));
            await File.WriteAllTextAsync(Path.Combine(folder.FullName, "modinfo.json"),
                "{\"modid\":\"folder\",\"name\":\"Folder Mod\",\"version\":\"1.2.0\"}");
            var zipPath = Path.Combine(mods.FullName, "old.zip");
            using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                var entry = archive.CreateEntry("modinfo.json");
                await using var writer = new StreamWriter(entry.Open());
                await writer.WriteAsync("{\"modid\":\"zip\",\"name\":\"Zip Mod\",\"version\":\"2.3.0\"}");
            }

            var service = new InstanceModService(new StubServerConfigService());
            var renamed = await service.NormalizeModNamesAsync(new InstanceProfile { DirectoryPath = directory.FullName });

            Assert.Equal(2, renamed);
            Assert.True(Directory.Exists(Path.Combine(mods.FullName, "Folder Mod-1.2.0")));
            Assert.True(File.Exists(Path.Combine(mods.FullName, "Zip Mod-2.3.0.zip")));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void ExtractModDirectory_FindsWrappedModRoot()
    {
        var directory = Directory.CreateTempSubdirectory("launchergo-mod-update-folder-");
        try
        {
            var zipPath = Path.Combine(directory.FullName, "update.zip");
            using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                using (var writer = new StreamWriter(archive.CreateEntry("release/My Mod/modinfo.json").Open()))
                    writer.Write("{\"modid\":\"mymod\",\"name\":\"My Mod\",\"version\":\"2.0\"}");
                using (var asset = new StreamWriter(archive.CreateEntry("release/My Mod/data.txt").Open()))
                    asset.Write("updated");
            }

            var root = InstanceModService.ExtractModDirectory(zipPath,
                Path.Combine(directory.FullName, "extracted"), "mymod");

            Assert.Equal("My Mod", Path.GetFileName(root));
            Assert.Equal("updated", File.ReadAllText(Path.Combine(root, "data.txt")));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private sealed class StubServerConfigService : IInstanceServerConfigService
    {
        public Task<ServerCommonSettings> LoadServerSettingsAsync(InstanceProfile profile, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<WorldSettings> LoadWorldSettingsAsync(InstanceProfile profile, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<WorldRuleValue>> LoadWorldRulesAsync(InstanceProfile profile, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task SaveSettingsAsync(
            InstanceProfile profile,
            ServerCommonSettings serverSettings,
            WorldSettings worldSettings,
            IReadOnlyList<WorldRuleValue> rules,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<string> LoadRawJsonAsync(InstanceProfile profile, CancellationToken cancellationToken = default) =>
            Task.FromResult("{}");

        public Task SaveRawJsonAsync(InstanceProfile profile, string json, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task ImportRawJsonAsync(InstanceProfile profile, string jsonFilePath, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
