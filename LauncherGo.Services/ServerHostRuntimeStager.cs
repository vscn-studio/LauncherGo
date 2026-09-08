using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text;

namespace LauncherGo.Services;

internal static class ServerHostRuntimeStager
{
    private const string CompletionMarkerName = ".complete";
    public static PreparedHost Prepare(string sourceExecutablePath, string? runtimeRoot = null,
        CancellationToken cancellationToken = default, IEnumerable<string>? additionalFiles = null,
        Action<string, TimeSpan>? progress = null)
    {
        var clock = Stopwatch.StartNew();
        void Checkpoint(string step)
        {
            progress?.Invoke(step, clock.Elapsed);
            cancellationToken.ThrowIfCancellationRequested();
        }
        Checkpoint("resolve-files");
        if (string.IsNullOrWhiteSpace(sourceExecutablePath) || !File.Exists(sourceExecutablePath))
            return new PreparedHost(sourceExecutablePath, null);

        var sourceDirectory = Path.GetDirectoryName(sourceExecutablePath);
        if (string.IsNullOrWhiteSpace(sourceDirectory))
            return new PreparedHost(sourceExecutablePath, null);

        runtimeRoot ??= WorkspacePathHelper.ServerHostRuntimeRoot;
        var files = ResolveRuntimeFiles(sourceDirectory, sourceExecutablePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var file in additionalFiles ?? [])
        {
            if (File.Exists(file) && IsWithinDirectory(file, sourceDirectory))
                files.Add(file);
        }
        Checkpoint("wait-cache-lock");
        using (CacheDirectoryLease.EnterRoot(runtimeRoot, wait: true, cancellationToken))
        {
            Checkpoint("hash-files");
            var versionKey = CreateVersionKey(sourceDirectory, runtimeRoot,
                files.OrderBy(path => path, StringComparer.OrdinalIgnoreCase), cancellationToken, Checkpoint);
            Checkpoint("validate-cache");
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
                    Checkpoint("copy-files");
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

                    Checkpoint("write-completion-marker");
                    File.WriteAllText(Path.Combine(temporaryDirectory, CompletionMarkerName), versionKey);
                    try
                    {
                        Checkpoint("move-cache-directory");
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

            Checkpoint("verify-staged-files");
            if (!IsStagedCopyComplete(sourceDirectory, targetDirectory, files, completionMarkerPath))
                throw new IOException($"Host runtime staging did not produce a complete copy: {targetDirectory}");

            Checkpoint("touch-completion-marker");
            File.SetLastWriteTimeUtc(completionMarkerPath, DateTime.UtcNow);
            Checkpoint("acquire-cache-lease");
            var lease = CacheDirectoryLease.Acquire(targetDirectory, cancellationToken);
            try
            {
                Checkpoint("ready");
                CacheMaintenance.Request(runtimeRoot, CacheKind.Host);
                return new PreparedHost(targetExecutablePath, lease);
            }
            catch { lease.Dispose(); throw; }
        }
    }

    public static IDisposable? AcquireCurrentLease()
    {
        var directory = AppContext.BaseDirectory;
        return File.Exists(Path.Combine(directory, CacheDirectoryLease.ProtocolMarker))
            ? CacheDirectoryLease.Acquire(directory) : null;
    }

    public sealed class PreparedHost : IDisposable
    {
        internal PreparedHost(string executablePath, CacheDirectoryLease? lease)
        {
            ExecutablePath = executablePath;
            this.lease = lease;
        }

        private readonly CacheDirectoryLease? lease;
        public string ExecutablePath { get; }
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
            var target = new FileInfo(Path.Combine(targetDirectory, relativePath));
            if (!target.Exists || target.Length != new FileInfo(sourcePath).Length)
                return false;
        }

        return true;
    }

    private static bool IsWithinDirectory(string file, string directory)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(file).StartsWith(root, StringComparison.OrdinalIgnoreCase);
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

    private sealed record CachedHash(FileStamp Stamp, string Hash);

    private static string CreateVersionKey(string sourceDirectory, string runtimeRoot, IEnumerable<string> files,
        CancellationToken cancellationToken, Action<string> checkpoint)
    {
        Directory.CreateDirectory(runtimeRoot);
        var cachePath = Path.Combine(runtimeRoot, ".source-hashes-v1.json");
        Dictionary<string, CachedHash>? cached = null;
        try
        {
            if (File.Exists(cachePath))
                cached = JsonSerializer.Deserialize<Dictionary<string, CachedHash>>(File.ReadAllText(cachePath));
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        cached ??= new();
        var updated = new Dictionary<string, CachedHash>();
        var readCount = 0;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = Path.GetFullPath(file);
            var stamp = FileStamp.Read(file);
            if (!cached.TryGetValue(key, out var entry) || entry is null || entry.Stamp != stamp ||
                entry.Hash is not { Length: 64 } || !entry.Hash.All(Uri.IsHexDigit))
            {
                using var contentHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                using var input = File.OpenRead(file);
                var buffer = new byte[64 * 1024];
                int read;
                while ((read = input.Read(buffer)) > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    contentHash.AppendData(buffer.AsSpan(0, read));
                }
                if (stamp != FileStamp.Read(file))
                    throw new IOException("Host source changed during preparation; retry after the update finishes.");
                entry = new(stamp, Convert.ToHexString(contentHash.GetHashAndReset()));
                readCount++;
            }
            updated[key] = entry;
            // Include file boundaries and relative names, not just concatenated contents.
            hash.AppendData(Encoding.UTF8.GetBytes(Path.GetRelativePath(sourceDirectory, file).Replace('\\', '/') + "\0"));
            hash.AppendData(Convert.FromHexString(entry.Hash));
        }
        checkpoint($"hash-files-result:read={readCount},reused={updated.Count - readCount}");
        cancellationToken.ThrowIfCancellationRequested();
        if (readCount != 0 || cached.Count != updated.Count)
        {
            var temporary = cachePath + $".{Guid.NewGuid():N}.tmp";
            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(updated));
                File.Move(temporary, cachePath, overwrite: true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
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
