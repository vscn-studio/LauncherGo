using ServerMap.Util;

namespace ServerMap.Render;

public static class TileColorStamp
{
    // Bump only when the basic rendering algorithm changes, not on every build.
    private const string Format = "client-colors-1:";
    public static bool IsCurrent(string tilePath, string colorVersion)
    {
        if (colorVersion == "fallback" || !File.Exists(tilePath)) return false;
        try { return File.ReadAllText(tilePath + ".colors") == Format + colorVersion; }
        catch (IOException) { return false; }
    }
    public static void Invalidate(string tilePath) => File.Delete(tilePath + ".colors");
    public static void Complete(string tilePath, string colorVersion) =>
        AtomicFile.Replace(tilePath + ".colors", temp => File.WriteAllText(temp, Format + colorVersion));
}
