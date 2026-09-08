namespace LauncherGo.Services;

// A local cache hint, not a signature/security check. Changes invalidate cached work.
internal sealed record FileStamp(long Length, long LastWriteTicks, long CreationTicks)
{
    internal static FileStamp Read(string path)
    {
        var file = new FileInfo(path);
        return new(file.Length, file.LastWriteTimeUtc.Ticks, file.CreationTimeUtc.Ticks);
    }
}
