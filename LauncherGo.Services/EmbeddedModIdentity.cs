using System.IO.Compression;
using System.Text.Json;

namespace LauncherGo.Services;

internal static class EmbeddedModIdentity
{
    private static readonly JsonDocumentOptions ModInfoJsonOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip
    };

    public static string GetDeploymentFolderName(string sourceRoot, string expectedId, string expectedVersion, string dllName)
    {
        var infoPath = Path.Combine(sourceRoot, "modinfo.json");
        if (!File.Exists(infoPath) || !File.Exists(Path.Combine(sourceRoot, dllName)))
            throw new InvalidOperationException($"内置模组文件不完整：{sourceRoot}");

        var json = File.ReadAllText(infoPath);
        var id = ReadField(json, "modid");
        var name = ReadField(json, "name");
        var version = ReadField(json, "version");
        if (!string.Equals(id, expectedId, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(version, expectedVersion, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(name))
            throw new InvalidOperationException($"内置模组元数据不匹配：{infoPath}");

        return BuildFolderName(name, expectedVersion);
    }

    public static void EnsureDestinationAvailable(string destination, string expectedId, string dllName)
    {
        if (!File.Exists(destination) && !Directory.Exists(destination)) return;
        var infoPath = Path.Combine(destination, "modinfo.json");
        var id = Directory.Exists(destination) && File.Exists(infoPath)
            ? ReadField(File.ReadAllText(infoPath), "modid")
            : null;
        if (string.Equals(id, expectedId, StringComparison.OrdinalIgnoreCase) ||
            (id is null && Directory.Exists(destination) && File.Exists(Path.Combine(destination, dllName))))
            return;
        throw new InvalidOperationException($"模组目标名称已被其他模组占用：{Path.GetFileName(destination)}");
    }

    public static string BuildFolderName(string name, string version)
    {
        var sanitized = WorkspacePathHelper.SanitizeFileName($"{name.Trim()}-{version.Trim()}").TrimEnd(' ', '.');
        return string.IsNullOrWhiteSpace(sanitized) ? "unnamed-unknown" : sanitized;
    }

    public static bool IsInstalled(string modsPath, params string[] ids)
    {
        if (!Directory.Exists(modsPath)) return false;
        foreach (var path in Directory.EnumerateFileSystemEntries(modsPath, "*", SearchOption.TopDirectoryOnly))
        {
            try
            {
                string? json = null;
                if (Directory.Exists(path))
                {
                    var infoPath = Path.Combine(path, "modinfo.json");
                    if (File.Exists(infoPath)) json = File.ReadAllText(infoPath);
                }
                else if (Path.GetExtension(path).Equals(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    using var archive = ZipFile.OpenRead(path);
                    var info = archive.Entries.FirstOrDefault(entry =>
                        entry.FullName.EndsWith("modinfo.json", StringComparison.OrdinalIgnoreCase));
                    if (info is not null)
                    {
                        using var reader = new StreamReader(info.Open());
                        json = reader.ReadToEnd();
                    }
                }

                if (json is not null && ids.Contains(ReadField(json, "modid"), StringComparer.OrdinalIgnoreCase))
                    return true;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (InvalidDataException) { }
        }

        return false;
    }

    private static string? ReadField(string json, string field)
    {
        try
        {
            using var document = JsonDocument.Parse(json, ModInfoJsonOptions);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
            foreach (var property in document.RootElement.EnumerateObject())
                if (property.Name.Equals(field, StringComparison.OrdinalIgnoreCase))
                    return property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : null;
        }
        catch (JsonException) { }

        return null;
    }
}
