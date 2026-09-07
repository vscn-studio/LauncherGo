using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace LauncherGo.Services;

internal static class ServerHostRuntimeStager
{
    private const string CompletionMarkerName = ".complete";
    private const int DefaultRetainedVersions = 0;
    private static readonly object Gate = new();

    public static string Prepare(string sourceExecutablePath, string? runtimeRoot = null)
    {
        if (string.IsNullOrWhiteSpace(sourceExecutablePath) || !File.Exists(sourceExecutablePath))
            return sourceExecutablePath;

        var sourceDirectory = Path.GetDirectoryName(sourceExecutablePath);
        if (string.IsNullOrWhiteSpace(sourceDirectory))
            return sourceExecutablePath;

        lock (Gate)
        {
            runtimeRoot ??= WorkspacePathHelper.ServerHostRuntimeRoot;
            var files = ResolveRuntimeFiles(sourceDirectory, sourceExecutablePath);
            var versionKey = CreateVersionKey(files);
            var targetDirectory = Path.Combine(runtimeRoot, versionKey);
            var targetExecutablePath = Path.Combine(targetDirectory, Path.GetFileName(sourceExecutablePath));
            var completionMarkerPath = Path.Combine(targetDirectory, CompletionMarkerName);

            if (!File.Exists(targetExecutablePath) || !File.Exists(completionMarkerPath))
            {
                TryDeleteIncompleteDirectory(targetDirectory);
                var temporaryDirectory = targetDirectory + $".{Guid.NewGuid():N}.tmp";
                try
                {
                    Directory.CreateDirectory(temporaryDirectory);
                    foreach (var sourcePath in files)
                    {
                        var relativePath = Path.GetRelativePath(sourceDirectory, sourcePath);
                        var targetPath = Path.Combine(temporaryDirectory, relativePath);
                        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                        File.Copy(sourcePath, targetPath, overwrite: true);
                    }

                    File.WriteAllText(Path.Combine(temporaryDirectory, CompletionMarkerName), versionKey);
                    try
                    {
                        Directory.Move(temporaryDirectory, targetDirectory);
                    }
                    catch (IOException) when (File.Exists(completionMarkerPath))
                    {
                        // Another LauncherGo process completed the same immutable version first.
                    }
                }
                finally
                {
                    TryDeleteIncompleteDirectory(temporaryDirectory);
                }
            }

            Cleanup(runtimeRoot, targetDirectory, DefaultRetainedVersions);

            return targetExecutablePath;
        }
    }

    /// <summary>
    /// Removes completed runtime copies that are not recent and are not used by a live process.
    /// </summary>
    internal static int Cleanup(string runtimeRoot, string? preserveDirectory = null, int retainCount = DefaultRetainedVersions)
    {
        if (string.IsNullOrWhiteSpace(runtimeRoot) || !Directory.Exists(runtimeRoot))
            return 0;

        var directories = Directory.EnumerateDirectories(runtimeRoot)
            .Select(static path => new DirectoryInfo(path))
            .Where(directory => File.Exists(Path.Combine(directory.FullName, CompletionMarkerName)))
            .OrderByDescending(static directory => directory.LastWriteTimeUtc)
            .ToList();
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(preserveDirectory))
            keep.Add(Path.GetFullPath(preserveDirectory));
        foreach (var directory in directories.Take(Math.Max(0, retainCount)))
            keep.Add(directory.FullName);

        var removed = 0;
        foreach (var directory in directories)
        {
            if (keep.Contains(directory.FullName) || IsUsedByLiveProcess(directory.FullName))
                continue;

            try
            {
                directory.Delete(recursive: true);
                removed++;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        return removed;
    }

    private static bool IsUsedByLiveProcess(string directory)
    {
        var normalizedDirectory = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                if (process.HasExited)
                    continue;
                var executablePath = process.MainModule?.FileName;
                if (!string.IsNullOrWhiteSpace(executablePath) &&
                    Path.GetFullPath(executablePath).StartsWith(normalizedDirectory, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            catch
            {
                // A process can exit or deny module inspection while the cleanup is running.
            }
            finally
            {
                process.Dispose();
            }
        }

        return false;
    }

    private static IReadOnlyList<string> ResolveRuntimeFiles(
        string sourceDirectory,
        string sourceExecutablePath)
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            sourceExecutablePath
        };
        var baseName = Path.GetFileNameWithoutExtension(sourceExecutablePath);
        var dependencyPath = Path.Combine(sourceDirectory, baseName + ".deps.json");
        var runtimeConfigPath = Path.Combine(sourceDirectory, baseName + ".runtimeconfig.json");

        AddIfExists(files, dependencyPath);
        AddIfExists(files, runtimeConfigPath);
        if (!File.Exists(dependencyPath))
            return files.OrderBy(static path => path, StringComparer.OrdinalIgnoreCase).ToList();

        using var document = JsonDocument.Parse(File.ReadAllText(dependencyPath));
        var root = document.RootElement;
        if (!root.TryGetProperty("runtimeTarget", out var runtimeTarget) ||
            !runtimeTarget.TryGetProperty("name", out var runtimeTargetName) ||
            !root.TryGetProperty("targets", out var targets) ||
            !targets.TryGetProperty(runtimeTargetName.GetString() ?? string.Empty, out var target))
        {
            throw new InvalidOperationException("ServerHost dependency manifest is invalid.");
        }

        foreach (var library in target.EnumerateObject())
        {
            AddManagedAssets(files, sourceDirectory, library.Value, "runtime");
            AddResourceAssets(files, sourceDirectory, library.Value);
            AddRuntimeTargets(files, sourceDirectory, library.Value);
        }

        return files.OrderBy(static path => path, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static void AddManagedAssets(
        HashSet<string> files,
        string sourceDirectory,
        JsonElement library,
        string groupName)
    {
        if (!library.TryGetProperty(groupName, out var assets))
            return;

        foreach (var asset in assets.EnumerateObject())
            AddIfExists(files, Path.Combine(sourceDirectory, Path.GetFileName(asset.Name)));
    }

    private static void AddResourceAssets(
        HashSet<string> files,
        string sourceDirectory,
        JsonElement library)
    {
        if (!library.TryGetProperty("resources", out var resources))
            return;

        foreach (var asset in resources.EnumerateObject())
        {
            if (!asset.Value.TryGetProperty("locale", out var locale))
                continue;

            AddIfExists(
                files,
                Path.Combine(sourceDirectory, locale.GetString() ?? string.Empty, Path.GetFileName(asset.Name)));
        }
    }

    private static void AddRuntimeTargets(
        HashSet<string> files,
        string sourceDirectory,
        JsonElement library)
    {
        if (!library.TryGetProperty("runtimeTargets", out var runtimeTargets))
            return;

        foreach (var asset in runtimeTargets.EnumerateObject())
        {
            if (!asset.Value.TryGetProperty("rid", out var ridElement) ||
                !IsRuntimeIdentifierCompatible(ridElement.GetString()))
            {
                continue;
            }

            var sourcePath = Path.Combine(
                sourceDirectory,
                asset.Name.Replace('/', Path.DirectorySeparatorChar));
            AddIfExists(files, sourcePath);
        }
    }

    private static bool IsRuntimeIdentifierCompatible(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
            return false;

        var current = RuntimeInformation.RuntimeIdentifier;
        return current.Equals(candidate, StringComparison.OrdinalIgnoreCase) ||
               current.StartsWith(candidate + "-", StringComparison.OrdinalIgnoreCase) ||
               candidate.Equals("win", StringComparison.OrdinalIgnoreCase) && OperatingSystem.IsWindows();
    }

    private static void AddIfExists(ISet<string> files, string path)
    {
        if (File.Exists(path))
            files.Add(path);
    }

    private static string CreateVersionKey(IEnumerable<string> files)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in files)
        {
            hash.AppendData(File.ReadAllBytes(file));
        }

        return Convert.ToHexString(hash.GetHashAndReset(), 0, 12).ToLowerInvariant();
    }

    private static void TryDeleteIncompleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch
        {
            // Another process may be preparing or using this immutable version.
        }
    }

}
