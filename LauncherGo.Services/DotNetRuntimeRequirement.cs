using System.Text.Json;
using Microsoft.Win32;

namespace LauncherGo.Services;

internal static class DotNetRuntimeRequirement
{
    // A single-file self-contained Host has no external runtimeconfig. Directory
    // self-contained builds have includedFrameworks instead of framework(s).
    public static void EnsureForHost(string hostPath)
    {
        var config = Path.ChangeExtension(hostPath, ".runtimeconfig.json");
        if (!File.Exists(config)) return;
        var requirements = ReadRequirements(File.ReadAllText(config));
        if (requirements.Count == 0 || !OperatingSystem.IsWindows()) return;
        var root = GetX64Root();
        IncludeAspNetCoreDependency(root, requirements);
        var missing = requirements.Where(pair => !HasCompatibleFramework(root, pair.Key, pair.Value)).ToArray();
        if (missing.Length == 0) return;
        throw new InvalidOperationException(
            "缺少 x64 .NET 运行时：" + string.Join(", ", missing.Select(p => $"{p.Key} {p.Value}")) +
            "。请重新运行 LauncherGo 安装程序以安装或修复运行时。 / " +
            "Required x64 .NET runtimes are missing. Run the LauncherGo installer again to repair them.");
    }

    internal static Dictionary<string, Version> ReadRequirements(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var result = new Dictionary<string, Version>();
        var options = doc.RootElement.GetProperty("runtimeOptions");
        void Add(JsonElement framework)
        {
            var name = framework.GetProperty("name").GetString()!;
            var version = Version.Parse(framework.GetProperty("version").GetString()!);
            if (!result.TryGetValue(name, out var previous) || version > previous) result[name] = version;
        }
        if (options.TryGetProperty("framework", out var single)) Add(single);
        if (options.TryGetProperty("frameworks", out var frameworks))
            foreach (var framework in frameworks.EnumerateArray()) Add(framework);
        return result;
    }

    internal static bool HasCompatibleFramework(string root, string name, Version required)
        => FindCompatibleFramework(root, name, required) is not null;

    internal static void IncludeAspNetCoreDependency(string root, Dictionary<string, Version> requirements)
    {
        if (!requirements.TryGetValue("Microsoft.AspNetCore.App", out var aspNet)) return;
        var selected = FindCompatibleFramework(root, "Microsoft.AspNetCore.App", aspNet);
        // The selected ASP.NET patch can require a newer Core patch than the
        // application's runtimeconfig. Keep both runtimes aligned, as setup does.
        var coreMinimum = selected ?? aspNet;
        if (!requirements.TryGetValue("Microsoft.NETCore.App", out var core) || core < coreMinimum)
            requirements["Microsoft.NETCore.App"] = coreMinimum;
    }

    private static Version? FindCompatibleFramework(string root, string name, Version required)
    {
        var directory = Path.Combine(root, "shared", name);
        if (!Directory.Exists(directory)) return null;
        return Directory.EnumerateDirectories(directory)
            .Where(path => Version.TryParse(Path.GetFileName(path), out var version) &&
                         version.Major == required.Major && version.Minor == required.Minor &&
                         version >= required && File.Exists(Path.Combine(path, name + ".deps.json")))
            .Select(path => Version.Parse(Path.GetFileName(path))).OrderDescending().FirstOrDefault();
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static string GetX64Root()
    {
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = machine.OpenSubKey(@"SOFTWARE\dotnet\Setup\InstalledVersions\x64");
        return key?.GetValue("InstallLocation") as string ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet");
    }
}
