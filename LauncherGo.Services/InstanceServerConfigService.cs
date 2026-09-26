using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using LauncherGo.Abstractions.Services;
using LauncherGo.Domains.Models;
using LauncherGo.Services.Paths;

namespace LauncherGo.Services;

public sealed class InstanceServerConfigService : IInstanceServerConfigService
{
    private static readonly JsonSerializerOptions JsonWriteOptions = new()
    {
        WriteIndented = true
    };

    public async Task<ServerCommonSettings> LoadServerSettingsAsync(
        InstanceProfile profile,
        CancellationToken cancellationToken = default)
    {
        var root = await LoadRootAsync(profile, cancellationToken);
        return new ServerCommonSettings
        {
            ServerName = ReadString(root["ServerName"], "Vintage Story Server"),
            ServerDescription = ReadNullableString(root["ServerDescription"]),
            ServerUrl = ReadNullableString(root["ServerUrl"]),
            Ip = ReadNullableString(root["Ip"]),
            Port = ReadInt(root["Port"], 42420),
            MaxClients = ReadInt(root["MaxClients"], 16),
            MaxClientsInQueue = ReadInt(root["MaxClientsInQueue"], 0),
            Password = ReadNullableString(root["Password"]),
            AdvertiseServer = ReadBool(root["AdvertiseServer"], false),
            WhitelistMode = ReadInt(root["WhitelistMode"], 0),
            Upnp = ReadBool(root["Upnp"], false),
            AllowPvP = ReadBool(root["AllowPvP"], true),
            AllowFireSpread = ReadBool(root["AllowFireSpread"], true),
            AllowFallingBlocks = ReadBool(root["AllowFallingBlocks"], true),
            PassTimeWhenEmpty = ReadBool(root["PassTimeWhenEmpty"], false),
            WarnClientsAfterAfkSeconds = ReadInt(root["WarnClientsAfterAfkSeconds"], 0),
            KickClientsAfterAfkSeconds = ReadInt(root["KickClientsAfterAfkSeconds"], 0),
            ClientConnectionTimeout = ReadInt(root["ClientConnectionTimeout"], 150),
            MaxChunkRadius = ReadInt(root["MaxChunkRadius"], 12),
            DieBelowDiskSpaceMb = ReadInt(root["DieBelowDiskSpaceMb"], 400),
            CorruptionProtection = ReadBool(root["CorruptionProtection"], true),
            RegenerateCorruptChunks = ReadBool(root["RegenerateCorruptChunks"], false),
            StartupCommands = ReadString(root["StartupCommands"], string.Empty),
            VerifyPlayerAuth = ReadBool(root["VerifyPlayerAuth"], true),
            ServerLanguage = ReadString(root["ServerLanguage"], ResolveDefaultServerLanguage()),
            DefaultRoleCode = ReadString(root["DefaultRoleCode"], "suplayer"),
            WelcomeMessage = ReadString(root["WelcomeMessage"], string.Empty)
        };
    }

    public async Task<WorldSettings> LoadWorldSettingsAsync(
        InstanceProfile profile,
        CancellationToken cancellationToken = default)
    {
        var root = await LoadRootAsync(profile, cancellationToken);
        var worldConfig = GetOrCreateObject(root, "WorldConfig");
        var worldRules = GetOrCreateObject(worldConfig, "WorldConfiguration");

        var mapSizeY = ReadNullableInt(worldConfig["MapSizeY"]) ?? ReadNullableInt(worldRules["worldHeight"]);
        return new WorldSettings
        {
            Seed = ReadString(worldConfig["Seed"], "123456789"),
            WorldName = ReadString(worldConfig["WorldName"], "A new world"),
            SaveFileLocation = ReadString(worldConfig["SaveFileLocation"], ResolveCurrentSaveFilePath(profile)),
            PlayStyle = ReadString(worldRules["playstyle"],
                ReadString(worldConfig["PlayStyle"], "surviveandbuild")),
            WorldType = ReadString(worldRules["worldtype"],
                ReadString(worldConfig["WorldType"], "standard")),
            WorldHeight = mapSizeY ?? 256
        };
    }

    public async Task<IReadOnlyList<WorldRuleValue>> LoadWorldRulesAsync(
        InstanceProfile profile,
        CancellationToken cancellationToken = default)
    {
        var root = await LoadRootAsync(profile, cancellationToken);
        var worldConfig = GetOrCreateObject(root, "WorldConfig");
        var worldRules = GetOrCreateObject(worldConfig, "WorldConfiguration");

        return WorldRuleCatalog.DefaultRules
            .Select(rule => new WorldRuleValue
            {
                Definition = rule,
                Value = ReadFlexibleString(worldRules[rule.Key])
                        ?? ReadRuleFallbackValue(rule.Key, root, worldConfig)
                        ?? rule.DefaultValue
            })
            .ToList();
    }

    public async Task SaveSettingsAsync(
        InstanceProfile profile,
        ServerCommonSettings serverSettings,
        WorldSettings worldSettings,
        IReadOnlyList<WorldRuleValue> rules,
        CancellationToken cancellationToken = default)
    {
        var root = await LoadRootAsync(profile, cancellationToken);

        root["ServerName"] = string.IsNullOrWhiteSpace(serverSettings.ServerName)
            ? "Vintage Story Server"
            : serverSettings.ServerName.Trim();
        root["ServerDescription"] = string.IsNullOrWhiteSpace(serverSettings.ServerDescription)
            ? null
            : serverSettings.ServerDescription.Trim();
        root["ServerUrl"] = string.IsNullOrWhiteSpace(serverSettings.ServerUrl)
            ? null
            : serverSettings.ServerUrl.Trim();
        root["Ip"] = string.IsNullOrWhiteSpace(serverSettings.Ip) ? null : serverSettings.Ip.Trim();
        root["Port"] = Math.Clamp(serverSettings.Port, 1, 65535);
        root["MaxClients"] = Math.Max(1, serverSettings.MaxClients);
        root["MaxClientsInQueue"] = Math.Max(0, serverSettings.MaxClientsInQueue);
        root["Password"] = string.IsNullOrWhiteSpace(serverSettings.Password) ? null : serverSettings.Password;
        root["AdvertiseServer"] = serverSettings.AdvertiseServer;
        root["WhitelistMode"] = Math.Clamp(serverSettings.WhitelistMode, 0, 2);
        root["Upnp"] = serverSettings.Upnp;
        root["AllowPvP"] = serverSettings.AllowPvP;
        root["AllowFireSpread"] = serverSettings.AllowFireSpread;
        root["AllowFallingBlocks"] = serverSettings.AllowFallingBlocks;
        root["PassTimeWhenEmpty"] = serverSettings.PassTimeWhenEmpty;
        root["WarnClientsAfterAfkSeconds"] = Math.Max(0, serverSettings.WarnClientsAfterAfkSeconds);
        root["KickClientsAfterAfkSeconds"] = Math.Max(0, serverSettings.KickClientsAfterAfkSeconds);
        root["ClientConnectionTimeout"] = Math.Max(1, serverSettings.ClientConnectionTimeout);
        root["MaxChunkRadius"] = Math.Max(1, serverSettings.MaxChunkRadius);
        root["DieBelowDiskSpaceMb"] = Math.Max(-1, serverSettings.DieBelowDiskSpaceMb);
        root["CorruptionProtection"] = serverSettings.CorruptionProtection;
        root["RegenerateCorruptChunks"] = serverSettings.RegenerateCorruptChunks;
        root["StartupCommands"] = string.IsNullOrWhiteSpace(serverSettings.StartupCommands)
            ? string.Empty
            : serverSettings.StartupCommands.Trim();
        root["VerifyPlayerAuth"] = serverSettings.VerifyPlayerAuth;
        root["ServerLanguage"] = string.IsNullOrWhiteSpace(serverSettings.ServerLanguage)
            ? ResolveDefaultServerLanguage()
            : serverSettings.ServerLanguage.Trim();
        root["DefaultRoleCode"] = string.IsNullOrWhiteSpace(serverSettings.DefaultRoleCode)
            ? "suplayer"
            : serverSettings.DefaultRoleCode.Trim();
        root["WelcomeMessage"] = string.IsNullOrWhiteSpace(serverSettings.WelcomeMessage)
            ? string.Empty
            : serverSettings.WelcomeMessage;
        root["ModPaths"] = BuildDefaultModPaths(profile);

        var worldConfig = GetOrCreateObject(root, "WorldConfig");
        worldConfig["Seed"] = string.IsNullOrWhiteSpace(worldSettings.Seed) ? "123456789" : worldSettings.Seed.Trim();
        worldConfig["WorldName"] = string.IsNullOrWhiteSpace(worldSettings.WorldName) ? "A new world" : worldSettings.WorldName.Trim();
        worldConfig["SaveFileLocation"] = string.IsNullOrWhiteSpace(worldSettings.SaveFileLocation)
            ? ResolveCurrentSaveFilePath(profile)
            : Path.GetFullPath(worldSettings.SaveFileLocation.Trim());
        worldConfig["PlayStyle"] = string.IsNullOrWhiteSpace(worldSettings.PlayStyle) ? "surviveandbuild" : worldSettings.PlayStyle.Trim();
        worldConfig["WorldType"] = string.IsNullOrWhiteSpace(worldSettings.WorldType) ? "standard" : worldSettings.WorldType.Trim();
        worldConfig["MapSizeY"] = Math.Clamp(worldSettings.WorldHeight ?? 256, 64, 2048);

        var worldRules = GetOrCreateObject(worldConfig, "WorldConfiguration");
        // Keep metadata fields in WorldConfig rather than leaving stale
        // lower-case export attributes behind after the form is saved.
        worldRules.Remove("playstyle");
        worldRules.Remove("worldtype");
        if (worldSettings.WorldHeight.HasValue)
        {
            worldRules["worldHeight"] = Math.Clamp(worldSettings.WorldHeight.Value, 64, 2048);
        }

        foreach (var rule in rules)
        {
            var normalizedValue = rule.Value?.Trim();
            if (string.IsNullOrWhiteSpace(normalizedValue))
            {
                continue;
            }

            if (rule.Definition.Key.Equals("worldWidth", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(normalizedValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var worldWidth))
            {
                root["MapSizeX"] = worldWidth;
                worldRules[rule.Definition.Key] = worldWidth;
                continue;
            }

            if (rule.Definition.Key.Equals("worldLength", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(normalizedValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var worldLength))
            {
                root["MapSizeZ"] = worldLength;
                worldRules[rule.Definition.Key] = worldLength;
                continue;
            }

            if (rule.Definition.Type == WorldRuleType.Boolean &&
                bool.TryParse(normalizedValue, out var boolValue))
            {
                worldRules[rule.Definition.Key] = boolValue;
            }
            else if (rule.Definition.Type == WorldRuleType.Number &&
                     int.TryParse(normalizedValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var intValue))
            {
                worldRules[rule.Definition.Key] = intValue;
            }
            else
            {
                worldRules[rule.Definition.Key] = normalizedValue;
            }
        }

        await SaveRootAsync(profile, root, cancellationToken);
    }

    public async Task<string> LoadRawJsonAsync(
        InstanceProfile profile,
        CancellationToken cancellationToken = default)
    {
        var root = await LoadRootAsync(profile, cancellationToken);
        return root.ToJsonString(JsonWriteOptions);
    }

    public async Task SaveRawJsonAsync(
        InstanceProfile profile,
        string json,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new InvalidOperationException("JSON 内容为空。");
        }

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"JSON 语法错误：{ex.Message}", ex);
        }

        if (node is not JsonObject root)
        {
            throw new InvalidOperationException("配置根节点必须是 JSON 对象。");
        }

        if (root["WorldConfig"] is not JsonObject)
        {
            // The game also exports world settings as a flat object (the
            // worldConfigAttributes format). Merge that form into the
            // profile's existing serverconfig instead of discarding server
            // settings such as ports, passwords and mod paths.
            var currentRoot = await LoadRootAsync(profile, cancellationToken);
            root = MergeFlatWorldConfig(currentRoot, root);
        }

        NormalizeImportedConfigPaths(profile, root);
        await SaveRootAsync(profile, root, cancellationToken);
    }

    public async Task ImportRawJsonAsync(
        InstanceProfile profile,
        string jsonFilePath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(jsonFilePath))
        {
            throw new InvalidOperationException("导入配置文件路径不能为空。");
        }

        var fullPath = Path.GetFullPath(jsonFilePath.Trim());
        if (!File.Exists(fullPath))
        {
            throw new InvalidOperationException($"导入配置文件不存在：{fullPath}");
        }

        if (!Path.GetExtension(fullPath).Equals(".json", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("仅支持导入 JSON 配置文件。");
        }

        var rawJson = await File.ReadAllTextAsync(fullPath, cancellationToken);
        await SaveRawJsonAsync(profile, rawJson, cancellationToken);
    }

    private async Task<JsonObject> LoadRootAsync(InstanceProfile profile, CancellationToken cancellationToken)
    {
        var configPath = LauncherWorkspacePathHelper.ProfileConfigPath(profile);
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);

        if (!File.Exists(configPath))
        {
            throw new FileNotFoundException($"配置文件不存在：{configPath}", configPath);
        }

        try
        {
            await using var stream = new FileStream(configPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var node = await JsonNode.ParseAsync(stream, cancellationToken: cancellationToken);
            return node as JsonObject
                   ?? throw new InvalidDataException($"配置根节点必须是 JSON 对象：{configPath}");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"配置文件无法解析，已保留原文件未覆盖：{configPath}", ex);
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new IOException($"配置文件读取失败：{configPath}。{ex.Message}", ex);
        }
    }

    private static async Task SaveRootAsync(InstanceProfile profile, JsonObject root, CancellationToken cancellationToken)
    {
        var configPath = LauncherWorkspacePathHelper.ProfileConfigPath(profile);
        await ServerConfigFileIO.WriteAllTextAtomicAsync(
            configPath,
            root.ToJsonString(JsonWriteOptions),
            cancellationToken);
    }

    private static void NormalizeImportedConfigPaths(InstanceProfile profile, JsonObject root)
    {
        root["ModPaths"] = BuildDefaultModPaths(profile);
        var worldConfig = GetOrCreateObject(root, "WorldConfig");
        worldConfig["SaveFileLocation"] = ResolveCurrentSaveFilePath(profile);
    }

    private static JsonObject MergeFlatWorldConfig(JsonObject currentRoot, JsonObject imported)
    {
        var worldConfig = GetOrCreateObject(currentRoot, "WorldConfig");
        var worldRules = GetOrCreateObject(worldConfig, "WorldConfiguration");

        // Some tools wrap the flat attributes in WorldConfiguration without
        // including the rest of serverconfig.json. Accept that form too.
        var source = imported["WorldConfiguration"] as JsonObject ?? imported;
        foreach (var property in source)
        {
            if (property.Value is null)
            {
                worldRules[property.Key] = null;
                continue;
            }

            // World configuration exports use lower-case attribute names,
            // while serverconfig.json stores these metadata fields under
            // WorldConfig with PascalCase names.
            if (property.Key.Equals("playstyle", StringComparison.OrdinalIgnoreCase))
            {
                worldConfig["PlayStyle"] = property.Value.DeepClone();
                worldRules.Remove(property.Key);
                continue;
            }

            if (property.Key.Equals("worldtype", StringComparison.OrdinalIgnoreCase))
            {
                worldConfig["WorldType"] = property.Value.DeepClone();
                worldRules.Remove(property.Key);
                continue;
            }

            switch (property.Key)
            {
                case "Seed":
                case "WorldName":
                case "PlayStyle":
                case "WorldType":
                case "SaveFileLocation":
                case "MapSizeY":
                    worldConfig[property.Key] = property.Value.DeepClone();
                    break;
                case "worldWidth":
                    var width = CloneIntegerValue(property.Value);
                    SetRootMapSize(currentRoot, "MapSizeX", width);
                    worldRules[property.Key] = width.DeepClone();
                    break;
                case "worldLength":
                    var length = CloneIntegerValue(property.Value);
                    SetRootMapSize(currentRoot, "MapSizeZ", length);
                    worldRules[property.Key] = length.DeepClone();
                    break;
                case "worldHeight":
                    var height = CloneIntegerValue(property.Value);
                    worldConfig["MapSizeY"] = height.DeepClone();
                    worldRules[property.Key] = height;
                    break;
                default:
                    worldRules[property.Key] = property.Value.DeepClone();
                    break;
            }
        }

        return currentRoot;
    }

    private static void SetRootMapSize(JsonObject root, string key, JsonNode value)
    {
        root[key] = value.DeepClone();
    }

    private static JsonNode CloneIntegerValue(JsonNode value)
    {
        return int.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? JsonValue.Create(parsed)!
            : value.DeepClone();
    }

    private static JsonArray BuildDefaultModPaths(InstanceProfile profile)
    {
        var modPaths = new JsonArray { "Mods" };
        var profileModsPath = LauncherWorkspacePathHelper.NormalizePath(Path.Combine(profile.DirectoryPath, "Mods"));
        if (!string.IsNullOrWhiteSpace(profileModsPath))
        {
            modPaths.Add(profileModsPath);
        }

        return modPaths;
    }

    private static string ResolveCurrentSaveFilePath(InstanceProfile profile)
    {
        var activeSaveFile = LauncherWorkspacePathHelper.NormalizePath(profile.ActiveSaveFile);
        var saveRoot = LauncherWorkspacePathHelper.NormalizePath(profile.SaveDirectory);
        if (!string.IsNullOrWhiteSpace(activeSaveFile) &&
            LauncherWorkspacePathHelper.IsSameOrChildPath(activeSaveFile, saveRoot))
        {
            return activeSaveFile;
        }

        if (!string.IsNullOrWhiteSpace(saveRoot))
        {
            return Path.Combine(saveRoot, "default.vcdbs");
        }

        return Path.Combine(profile.DirectoryPath, "Saves", "default.vcdbs");
    }

    private static JsonObject GetOrCreateObject(JsonObject root, string propertyName)
    {
        if (root[propertyName] is JsonObject obj)
        {
            return obj;
        }

        var created = new JsonObject();
        root[propertyName] = created;
        return created;
    }

    private static string ReadString(JsonNode? node, string defaultValue)
    {
        return node?.GetValue<string>() ?? defaultValue;
    }

    private static string? ReadNullableString(JsonNode? node)
    {
        return node is null ? null : node.GetValue<string?>();
    }

    private static int ReadInt(JsonNode? node, int defaultValue)
    {
        if (node is null)
        {
            return defaultValue;
        }

        if (node.GetValueKind() == JsonValueKind.Number &&
            node is JsonValue numericValue &&
            numericValue.TryGetValue<int>(out var value))
        {
            return value;
        }

        if (node.GetValueKind() == JsonValueKind.String &&
            int.TryParse(node.GetValue<string>(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
        {
            return value;
        }

        return defaultValue;
    }

    private static int? ReadNullableInt(JsonNode? node)
    {
        if (node is null)
        {
            return null;
        }

        if (node.GetValueKind() == JsonValueKind.Number &&
            node is JsonValue numericValue &&
            numericValue.TryGetValue<int>(out var numeric))
        {
            return numeric;
        }

        if (node.GetValueKind() == JsonValueKind.String &&
            int.TryParse(node.GetValue<string>(), NumberStyles.Integer, CultureInfo.InvariantCulture, out numeric))
        {
            return numeric;
        }

        return null;
    }

    private static bool ReadBool(JsonNode? node, bool defaultValue)
    {
        if (node is null)
        {
            return defaultValue;
        }

        if (node.GetValueKind() == JsonValueKind.True || node.GetValueKind() == JsonValueKind.False)
        {
            return node.GetValue<bool>();
        }

        if (node.GetValueKind() == JsonValueKind.String &&
            bool.TryParse(node.GetValue<string>(), out var parsed))
        {
            return parsed;
        }

        return defaultValue;
    }

    private static string? ReadFlexibleString(JsonNode? node)
    {
        if (node is null)
        {
            return null;
        }

        return node.GetValueKind() switch
        {
            JsonValueKind.String => node.GetValue<string>(),
            JsonValueKind.True => bool.TrueString.ToLowerInvariant(),
            JsonValueKind.False => bool.FalseString.ToLowerInvariant(),
            JsonValueKind.Number => node.ToString(),
            _ => node.ToJsonString()
        };
    }

    private static string? ReadRuleFallbackValue(string key, JsonObject root, JsonObject worldConfig)
    {
        return key switch
        {
            "worldWidth" => ReadFlexibleString(root["MapSizeX"]) ?? ReadFlexibleString(worldConfig["MapSizeX"]),
            "worldLength" => ReadFlexibleString(root["MapSizeZ"]) ?? ReadFlexibleString(worldConfig["MapSizeZ"]),
            "colorAccurateWorldmap" => ReadFlexibleString(worldConfig["colorAccurateWorldmap"]),
            _ => null
        };
    }

    private static string ResolveDefaultServerLanguage()
    {
        return CultureInfo.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? "zh-cn" : "en";
    }
}
