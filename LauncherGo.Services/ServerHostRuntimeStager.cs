using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace LauncherGo.Services;

internal static class ServerHostRuntimeStager
{
    private const string CompletionMarkerName = ".complete";
    public static PreparedHost Prepare(string sourceExecutablePath, string? runtimeRoot = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourceExecutablePath) || !File.Exists(sourceExecutablePath))
            return new PreparedHost(sourceExecutablePath, null);

        var sourceDirectory = Path.GetDirectoryName(sourceExecutablePath);
        if (string.IsNullOrWhiteSpace(sourceDirectory))
            return new PreparedHost(sourceExecutablePath, null);

        runtimeRoot ??= WorkspacePathHelper.ServerHostRuntimeRoot;
        var files = ResolveRuntimeFiles(sourceDirectory, sourceExecutablePath);
        var versionKey = CreateVersionKey(files, cancellationToken);
        using (CacheDirectoryLease.EnterRoot(runtimeRoot, wait: true, cancellationToken))
        {
            var targetDirectory = Path.Combine(runtimeRoot, versionKey);
            var targetExecutablePath = Path.Combine(targetDirectory, Path.GetFileName(sourceExecutablePath));
            var completionMarkerPath = Path.Combine(targetDirectory, CompletionMarkerName);

            if (!IsStagedCopyComplete(sourceDirectory, targetDirectory, files, completionMarkerPath))
            {
                // A Host may still be using the other files in an incomplete directory.
                if (Directory.Exists(targetDirectory))
                {
                    targetDirectory = Path.Combine(runtimeRoot, $"{versionKey}-{Guid.NewGuid():N}");
                    targetExecutablePath = Path.Combine(targetDirectory, Path.GetFileName(sourceExecutablePath));
                    completionMarkerPath = Path.Combine(targetDirectory, CompletionMarkerName);
                }

                var temporaryDirectory = targetDirectory + $".{Guid.NewGuid():N}.tmp";
                try
                {
                    Directory.CreateDirectory(temporaryDirectory);
                    using (File.Create(Path.Combine(temporaryDirectory, CacheDirectoryLease.FileName))) { }
                    foreach (var sourcePath in files)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
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
                    catch (IOException) when (IsStagedCopyComplete(
                               sourceDirectory,
                               targetDirectory,
                               files,
                               completionMarkerPath))
                    {
                        // Another LauncherGo process completed the same immutable version first.
                    }
                }
                finally
                {
                    TryDeleteIncompleteDirectory(temporaryDirectory);
                }
            }

            if (!IsStagedCopyComplete(sourceDirectory, targetDirectory, files, completionMarkerPath))
                throw new IOException($"Host runtime staging did not produce a complete copy: {targetDirectory}");

            File.SetLastWriteTimeUtc(completionMarkerPath, DateTime.UtcNow);
            var lease = CacheDirectoryLease.Acquire(targetDirectory);
            CacheMaintenance.Request(runtimeRoot, CacheKind.Host);
            return new PreparedHost(targetExecutablePath, lease);
        }
    }

    internal static IDisposable? AcquireCurrentLease()
    {
        var directory = AppContext.BaseDirectory;
        return File.Exists(Path.Combine(directory, CacheDirectoryLease.ProtocolMarker))
            ? CacheDirectoryLease.Acquire(directory) : null;
    }

    internal sealed class PreparedHost(string executablePath, CacheDirectoryLease? lease) : IDisposable
    {
        public string ExecutablePath { get; } = executablePath;
        public void Dispose()
        {
            lease?.Dispose();
            if (lease is not null) CacheMaintenance.Request(Path.GetDirectoryName(lease.DirectoryPath)!, CacheKind.Host);
        }
    }

    private static bool IsStagedCopyComplete(
        string sourceDirectory,
        string targetDirectory,
        IEnumerable<string> sourceFiles,
        string completionMarkerPath)
    {
        if (!File.Exists(completionMarkerPath) || !File.Exists(Path.Combine(targetDirectory, CacheDirectoryLease.FileName)))
            return false;

        foreach (var sourcePath in sourceFiles)
        {
            var relativePath = Path.GetRelativePath(sourceDirectory, sourcePath);
            if (!File.Exists(Path.Combine(targetDirectory, relativePath)))
                return false;
        }

        return true;
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

    private static string CreateVersionKey(IEnumerable<string> files, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var input = File.OpenRead(file);
            int read;
            while ((read = input.Read(buffer)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                hash.AppendData(buffer.AsSpan(0, read));
            }
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
