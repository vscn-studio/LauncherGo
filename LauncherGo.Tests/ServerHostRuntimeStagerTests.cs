using System.Diagnostics;
using LauncherGo.Services;
using Xunit;

namespace LauncherGo.Tests;

public sealed class ServerHostRuntimeStagerTests
{
    [Fact]
    public void Prepare_StagesSingleFileHostOutsideBuildOutput()
    {
        var sourceDirectory = Path.Combine(Path.GetTempPath(), $"launchergo-host-source-{Guid.NewGuid():N}");
        var runtimeRoot = Path.Combine(Path.GetTempPath(), $"launchergo-host-runtime-{Guid.NewGuid():N}");
        var sourcePath = Path.Combine(sourceDirectory, "LauncherGo.ServerHost.exe");

        try
        {
            Directory.CreateDirectory(sourceDirectory);
            File.WriteAllText(sourcePath, "single-file-host");

            using var prepared = ServerHostRuntimeStager.Prepare(sourcePath, runtimeRoot);
            var stagedPath = prepared.ExecutablePath;

            Assert.True(File.Exists(stagedPath));
            Assert.NotEqual(Path.GetFullPath(sourcePath), Path.GetFullPath(stagedPath));
            Assert.StartsWith(
                Path.GetFullPath(runtimeRoot),
                Path.GetFullPath(stagedPath),
                StringComparison.OrdinalIgnoreCase);
            Assert.Equal("single-file-host", File.ReadAllText(stagedPath));

            File.Delete(stagedPath);
            using var rebuilt = ServerHostRuntimeStager.Prepare(sourcePath, runtimeRoot);
            var rebuiltPath = rebuilt.ExecutablePath;
            Assert.True(File.Exists(rebuiltPath));
            Assert.Equal("single-file-host", File.ReadAllText(rebuiltPath));
        }
        finally
        {
            Directory.Delete(sourceDirectory, recursive: true);
            if (Directory.Exists(runtimeRoot))
                Directory.Delete(runtimeRoot, recursive: true);
        }
    }

    [Fact]
    public void Prepare_RepairsMissingDependencyDespiteCompletionMarker()
    {
        var sourceDirectory = Path.Combine(Path.GetTempPath(), $"launchergo-host-source-{Guid.NewGuid():N}");
        var runtimeRoot = Path.Combine(Path.GetTempPath(), $"launchergo-host-runtime-{Guid.NewGuid():N}");
        var sourcePath = Path.Combine(sourceDirectory, "LauncherGo.ServerHost.exe");
        var dependencyPath = Path.Combine(sourceDirectory, "LauncherGo.ServerHost.runtimeconfig.json");

        try
        {
            Directory.CreateDirectory(sourceDirectory);
            File.WriteAllText(sourcePath, "single-file-host");
            File.WriteAllText(dependencyPath, "runtime-config");

            using var prepared = ServerHostRuntimeStager.Prepare(sourcePath, runtimeRoot);
            var stagedPath = prepared.ExecutablePath;
            var stagedDependency = Path.Combine(Path.GetDirectoryName(stagedPath)!, Path.GetFileName(dependencyPath));
            File.Delete(stagedDependency);

            using var rebuilt = ServerHostRuntimeStager.Prepare(sourcePath, runtimeRoot);
            var rebuiltPath = rebuilt.ExecutablePath;

            Assert.True(File.Exists(rebuiltPath));
            Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(rebuiltPath)!, Path.GetFileName(dependencyPath))));
        }
        finally
        {
            Directory.Delete(sourceDirectory, recursive: true);
            if (Directory.Exists(runtimeRoot))
                Directory.Delete(runtimeRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Prepare_StagesFrameworkDependentHostWithRunnableDependencies()
    {
        var projectOutput = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "LauncherGo.ServerHost",
            "bin",
            "Release",
            "net10.0"));
        var sourcePath = Path.Combine(projectOutput, "LauncherGo.ServerHost.exe");
        var runtimeRoot = Path.Combine(Path.GetTempPath(), $"launchergo-host-runtime-{Guid.NewGuid():N}");

        Assert.True(File.Exists(sourcePath), $"ServerHost build output not found: {sourcePath}");

        try
        {
            using var prepared = ServerHostRuntimeStager.Prepare(sourcePath, runtimeRoot);
            var stagedPath = prepared.ExecutablePath;
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = stagedPath,
                WorkingDirectory = Path.GetDirectoryName(stagedPath)!,
                UseShellExecute = false,
                CreateNoWindow = true
            });

            Assert.NotNull(process);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.NotEqual(unchecked((int)0xE0434352), process.ExitCode);
            Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(stagedPath)!, "LauncherGo.Services.dll")));
            Assert.True(File.Exists(Path.Combine(
                Path.GetDirectoryName(stagedPath)!,
                "runtimes",
                "win",
                "lib",
                "net10.0",
                "System.Management.dll")));
            Assert.True(File.Exists(Path.Combine(
                Path.GetDirectoryName(stagedPath)!,
                "runtimes",
                System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier,
                "native",
                "e_sqlite3.dll")));
        }
        finally
        {
            if (Directory.Exists(runtimeRoot))
                Directory.Delete(runtimeRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Cleanup_RemovesOldCompletedCopiesAndKeepsRecentCopy()
    {
        var runtimeRoot = Path.Combine(Path.GetTempPath(), $"launchergo-host-cleanup-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(runtimeRoot);
            for (var index = 0; index < 3; index++)
            {
                var directory = Path.Combine(runtimeRoot, $"version-{index}");
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, ".complete"), index.ToString());
                File.SetLastWriteTimeUtc(Path.Combine(directory, ".complete"), DateTime.UtcNow.AddMinutes(-index * 10));
            }

            var result = await CacheMaintenance.CleanAsync(runtimeRoot, CacheKind.Host, default);

            Assert.Equal(CacheCleanupResult.RetryLater, result);
            Assert.True(Directory.Exists(Path.Combine(runtimeRoot, "version-0")));
            Assert.False(Directory.Exists(Path.Combine(runtimeRoot, "version-1")));
            Assert.False(Directory.Exists(Path.Combine(runtimeRoot, "version-2")));
        }
        finally
        {
            if (Directory.Exists(runtimeRoot))
                Directory.Delete(runtimeRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Cleanup_DoesNotDeleteHostDuringStartupGracePeriod()
    {
        var runtimeRoot = Path.Combine(Path.GetTempPath(), $"launchergo-host-cleanup-{Guid.NewGuid():N}");
        var directory = Path.Combine(runtimeRoot, "newly-staged");
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, ".complete"), "newly-staged");

            var result = await CacheMaintenance.CleanAsync(runtimeRoot, CacheKind.Host, default);

            Assert.Equal(CacheCleanupResult.RetryLater, result);
            Assert.True(Directory.Exists(directory));
        }
        finally
        {
            if (Directory.Exists(runtimeRoot))
                Directory.Delete(runtimeRoot, recursive: true);
        }
    }
}
