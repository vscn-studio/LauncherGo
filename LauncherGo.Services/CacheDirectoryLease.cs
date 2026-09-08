using System.Security.Cryptography;
using System.Text;

namespace LauncherGo.Services;

internal sealed class CacheDirectoryLease : IDisposable
{
    internal const string FileName = ".use-lock";
    internal const string ProtocolMarker = ".lease-v1";
    private readonly FileStream _stream;

    private CacheDirectoryLease(string directory, FileStream stream)
    {
        DirectoryPath = directory;
        _stream = stream;
    }

    public string DirectoryPath { get; }

    // Callers creating a directory hold the root gate until its first lease is acquired.
    internal static CacheDirectoryLease Acquire(string directory, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        using var gate = EnterRoot(Path.GetDirectoryName(directory)!, wait: true, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var path = Path.Combine(directory, FileName);
        if (!File.Exists(path))
            using (File.Create(path)) { }
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(Path.Combine(directory, ProtocolMarker)))
                File.WriteAllText(Path.Combine(directory, ProtocolMarker), "1");
            cancellationToken.ThrowIfCancellationRequested();
            return new CacheDirectoryLease(directory, stream);
        }
        catch { stream.Dispose(); throw; }
    }

    internal static FileStream? TryAcquireForCleanup(string directory, string fileName = FileName)
    {
        try
        {
            return new FileStream(Path.Combine(directory, fileName), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.Delete);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    internal static IDisposable? EnterRoot(string root, bool wait, CancellationToken token = default)
    {
        var normalized = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        if (OperatingSystem.IsWindows()) normalized = normalized.ToUpperInvariant();
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
        var mutex = new Mutex(false, "Global\\LauncherGo.Cache." + key);
        try
        {
            bool acquired;
            try
            {
                acquired = wait
                    ? WaitHandle.WaitAny([mutex, token.WaitHandle]) == 0
                    : mutex.WaitOne(0);
            }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired)
            {
                mutex.Dispose();
                token.ThrowIfCancellationRequested();
                return null;
            }
            return new MutexScope(mutex);
        }
        catch { mutex.Dispose(); throw; }
    }

    public void Dispose() => _stream.Dispose();

    private sealed class MutexScope(Mutex mutex) : IDisposable
    {
        public void Dispose() { mutex.ReleaseMutex(); mutex.Dispose(); }
    }
}
