namespace ServerMap.Util;

public static class AtomicFile
{
    public static void Replace(string path, Action<string> write)
    {
        var directory = Path.GetDirectoryName(path) ?? throw new InvalidOperationException("Output path has no directory");
        Directory.CreateDirectory(directory);
        var temp = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            write(temp);
            for (var attempt = 0; ; attempt++)
            {
                try { File.Move(temp, path, true); return; }
                catch (IOException) when (attempt < 20) { Thread.Sleep(50 * Math.Min(attempt + 1, 4)); }
            }
        }
        finally { try { if (File.Exists(temp)) File.Delete(temp); } catch { } }
    }
}
