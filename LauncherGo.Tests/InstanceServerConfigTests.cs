using System.Reflection;
using System.Text.Json.Nodes;
using LauncherGo.Domains.Models;
using LauncherGo.Services;
using LauncherGo.Ui.Views;
using Xunit;

namespace LauncherGo.Tests;

public sealed class InstanceServerConfigTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("launchergo-config-test-").FullName;
    private readonly InstanceServerConfigService _service = new();

    private InstanceProfile Profile => new()
    {
        DirectoryPath = _directory,
        SaveDirectory = Path.Combine(_directory, "Saves"),
        ActiveSaveFile = Path.Combine(_directory, "Saves", "default.vcdbs")
    };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GameExportImportsWorldSettingsAndPreservesProfileAndModRules(bool wrapped)
    {
        await WriteConfigAsync("""
            {"ServerName":"Keep server","Port":42421,"WorldConfig":{
                "PlayStyle":"surviveandbuild","WorldType":"standard",
                "WorldConfiguration":{"keepModRule":"keep","deathPunishment":"drop"}}}
            """);
        var imported = JsonNode.Parse("""
            {"playstyle":"homosapiens","gameMode":"survival","worldHeight":384,
             "worldWidth":"1024000","worldLength":"512000","deathPunishment":"keep",
             "exwWeatherEvents":true,"exwEventGraceDays":"7","scribeDeliveryRadius":200}
            """)!;
        var importPath = Path.Combine(_directory, "export.json");
        await File.WriteAllTextAsync(importPath,
            (wrapped ? new JsonObject { ["WorldConfiguration"] = imported } : imported).ToJsonString());

        await _service.ImportRawJsonAsync(Profile, importPath);

        var root = JsonNode.Parse(await _service.LoadRawJsonAsync(Profile))!;
        var world = root["WorldConfig"]!;
        var rules = world["WorldConfiguration"]!;
        Assert.Equal("Keep server", root["ServerName"]!.GetValue<string>());
        Assert.Equal(42421, root["Port"]!.GetValue<int>());
        Assert.Equal("homosapiens", world["PlayStyle"]!.GetValue<string>());
        Assert.Equal(384, world["MapSizeY"]!.GetValue<int>());
        Assert.Equal(1024000, root["MapSizeX"]!.GetValue<int>());
        Assert.Equal(512000, root["MapSizeZ"]!.GetValue<int>());
        Assert.Equal(Profile.ActiveSaveFile, world["SaveFileLocation"]!.GetValue<string>());
        Assert.Equal("keep", rules["keepModRule"]!.GetValue<string>());
        Assert.Equal("keep", rules["deathPunishment"]!.GetValue<string>());
        Assert.Equal("survival", rules["gameMode"]!.GetValue<string>());
        Assert.True(rules["exwWeatherEvents"]!.GetValue<bool>());
        Assert.Equal("7", rules["exwEventGraceDays"]!.GetValue<string>());
        Assert.Equal(200, rules["scribeDeliveryRadius"]!.GetValue<int>());
        Assert.Equal("homosapiens", ReadWorldSettingsForUi(root).PlayStyle);
    }

    [Fact]
    public async Task PreviouslyImportedPlayStyleLoadsInServiceAndFormAndSurvivesSave()
    {
        await WriteConfigAsync("""
            {"WorldConfig":{"PlayStyle":"surviveandbuild","WorldType":"standard",
                "WorldConfiguration":{"playstyle":"homosapiens","worldtype":"custom"}}}
            """);

        var world = await _service.LoadWorldSettingsAsync(Profile);
        var root = JsonNode.Parse(await _service.LoadRawJsonAsync(Profile))!;
        var formWorld = ReadWorldSettingsForUi(root);
        Assert.Equal("homosapiens", world.PlayStyle);
        Assert.Equal(world.PlayStyle, formWorld.PlayStyle);
        Assert.Equal("custom", world.WorldType);
        Assert.Equal(world.WorldType, formWorld.WorldType);

        await _service.SaveSettingsAsync(Profile,
            await _service.LoadServerSettingsAsync(Profile), formWorld,
            await _service.LoadWorldRulesAsync(Profile));

        root = JsonNode.Parse(await _service.LoadRawJsonAsync(Profile))!;
        Assert.Null(root["WorldConfig"]!["WorldConfiguration"]!["playstyle"]);
        Assert.Null(root["WorldConfig"]!["WorldConfiguration"]!["worldtype"]);
        var reloaded = await _service.LoadWorldSettingsAsync(Profile);
        Assert.Equal("homosapiens", reloaded.PlayStyle);
        Assert.Equal("custom", reloaded.WorldType);
        Assert.Equal(reloaded.PlayStyle, ReadWorldSettingsForUi(root).PlayStyle);
    }

    private WorldSettings ReadWorldSettingsForUi(JsonNode root)
    {
        var method = typeof(LauncherMainWindow).GetMethod(
            "BuildConfigWorldSettings", BindingFlags.NonPublic | BindingFlags.Static)!;
        return (WorldSettings)method.Invoke(null, [Profile, root])!;
    }

    private Task WriteConfigAsync(string json) =>
        File.WriteAllTextAsync(Path.Combine(_directory, "serverconfig.json"), json);

    public void Dispose()
    {
        var path = Path.GetFullPath(_directory);
        var tempRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()))
                       + Path.DirectorySeparatorChar;
        if (path.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase) &&
            Path.GetFileName(path).StartsWith("launchergo-config-test-", StringComparison.Ordinal))
        {
            Directory.Delete(path, recursive: true);
        }
    }
}
