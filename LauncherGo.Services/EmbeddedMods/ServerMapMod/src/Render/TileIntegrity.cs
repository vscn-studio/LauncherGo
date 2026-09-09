using System.Security.Cryptography;
using ServerMap.Util;

namespace ServerMap.Render;

public static class TileIntegrity
{
    public static void Write(string path, byte[] bytes)
    {
        var digest = Convert.ToHexString(SHA256.HashData(bytes));
        AtomicFile.Replace(path, temp => File.WriteAllBytes(temp, bytes));
        AtomicFile.Replace(path + ".sha256", temp => File.WriteAllText(temp, digest));
    }
    public static bool IsValid(string path)
    {
        try { using var stream = File.OpenRead(path); return File.ReadAllText(path + ".sha256") == Convert.ToHexString(SHA256.HashData(stream)); }
        catch (IOException) { return false; }
    }
}
