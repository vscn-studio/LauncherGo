using System.IO.Compression;
using System.Security.Cryptography;
using ServerMap.Util;

namespace ServerMap.Render;

// Code dictionaries survive block id reassignment between server processes.
public sealed class SurfaceRegion(int width = 512)
{
    public int Width { get; } = width;
    public const int Format = 1, Size = 512, Pixels = Size * Size;
    public ushort[] Heights { get; } = new ushort[width * width];
    public bool[] Valid { get; } = new bool[width * width];
    public string[] Codes { get; } = Enumerable.Repeat("game:air", width * width).ToArray();
    public string[] EntityKeys { get; } = new string[width * width];
    public string[] SepiaKeys { get; } = new string[width * width];
    public bool[] Water { get; } = new bool[width * width];
    public bool[] Columns { get; } = new bool[256];
    public string[] Fingerprints { get; } = new string[256];
    public long Generation { get; set; }
    public bool AwaitingSave { get; set; }
    public string ContentVersion { get; set; } = "";
    public string ObjectVersion { get; set; } = "";
    public static string PathFor(string root, int x, int z) => Path.Combine(root, "surface", $"{x}_{z}.br");
    public SurfaceRegion Column(int index)
    {
        var column = new SurfaceRegion(32) { Generation = Generation };
        column.Columns[index] = Columns[index]; column.Fingerprints[index] = Fingerprints[index];
        for (var x = 0; x < 32; x++) for (var z = 0; z < 32; z++) CopyPixel(this, (index % 16 * 32 + z) * 512 + index / 16 * 32 + x, column, z * 32 + x);
        return column;
    }
    public bool MatchesColumn(int index, SurfaceRegion column)
    {
        if (Columns[index] != column.Columns[index] || Fingerprints[index] != column.Fingerprints[index]) return false;
        for (var x = 0; x < 32; x++) for (var z = 0; z < 32; z++)
        {
            var i = (index % 16 * 32 + z) * 512 + index / 16 * 32 + x; var j = z * 32 + x;
            if (Heights[i] != column.Heights[j] || Valid[i] != column.Valid[j] || Codes[i] != column.Codes[j]
                || (EntityKeys[i] ?? "") != (column.EntityKeys[j] ?? "") || (SepiaKeys[i] ?? "") != (column.SepiaKeys[j] ?? "") || Water[i] != column.Water[j]) return false;
        }
        return true;
    }
    public void MergeColumn(int index, SurfaceRegion column)
    {
        if (Width != 512 || column.Width != 32) throw new InvalidDataException("Invalid column dimensions");
        Columns[index] = column.Columns[index]; Fingerprints[index] = column.Fingerprints[index];
        for (var x = 0; x < 32; x++) for (var z = 0; z < 32; z++) CopyPixel(column, z * 32 + x, this, (index % 16 * 32 + z) * 512 + index / 16 * 32 + x);
    }
    private static void CopyPixel(SurfaceRegion source, int from, SurfaceRegion target, int to)
    {
        target.Heights[to] = source.Heights[from]; target.Valid[to] = source.Valid[from]; target.Codes[to] = source.Codes[from];
        target.EntityKeys[to] = source.EntityKeys[from]; target.SepiaKeys[to] = source.SepiaKeys[from]; target.Water[to] = source.Water[from];
    }
    public void Save(string path)
    {
        using var data = new MemoryStream();
        using (var w = new BinaryWriter(data, System.Text.Encoding.UTF8, true))
        {
            w.Write(Format); w.Write(Width); w.Write(Generation); w.Write(AwaitingSave); w.Write(ObjectVersion); w.Write(ContentVersion);
            var dictionary = Codes.Concat(EntityKeys).Concat(SepiaKeys).Select(s => s ?? "").Distinct(StringComparer.Ordinal).ToArray();
            var lookup = dictionary.Select((s, i) => (s, i)).ToDictionary(p => p.s, p => p.i, StringComparer.Ordinal);
            w.Write(dictionary.Length); foreach (var code in dictionary) w.Write(code);
            for (var i = 0; i < 256; i++) { w.Write(Columns[i]); w.Write(Fingerprints[i] ?? ""); }
            for (var i = 0; i < Heights.Length; i++) { w.Write(Heights[i]); w.Write(Valid[i]); w.Write(lookup[Codes[i]]); w.Write(lookup[EntityKeys[i] ?? ""]); w.Write(lookup[SepiaKeys[i] ?? ""]); w.Write(Water[i]); }
        }
        var bytes = data.ToArray(); var digest = SHA256.HashData(bytes);
        AtomicFile.Replace(path, temp => { using var file = File.Create(temp); file.Write(digest); using var br = new BrotliStream(file, CompressionLevel.Fastest); br.Write(bytes); });
    }
    public static SurfaceRegion? Load(string path)
    {
        try
        {
            using var file = File.OpenRead(path); var digest = new byte[32]; file.ReadExactly(digest);
            using var br = new BrotliStream(file, CompressionMode.Decompress); using var data = new MemoryStream();
            var buffer = new byte[81920]; int read;
            while ((read = br.Read(buffer)) > 0) { if (data.Length + read > 32 * 1024 * 1024) return null; data.Write(buffer, 0, read); }
            if (!SHA256.HashData(data.ToArray()).SequenceEqual(digest)) return null;
            data.Position = 0; using var r = new BinaryReader(data);
            if (r.ReadInt32() != Format) return null;
            var width = r.ReadInt32(); if (width != 32 && width != 512) return null;
            var surface = new SurfaceRegion(width) { Generation = r.ReadInt64(), AwaitingSave = r.ReadBoolean(), ObjectVersion = r.ReadString(), ContentVersion = r.ReadString() };
            var count = r.ReadInt32(); if (count < 1 || count > Pixels * 3) return null;
            var codes = Enumerable.Range(0, count).Select(_ => r.ReadString()).ToArray();
            for (var i = 0; i < 256; i++) { surface.Columns[i] = r.ReadBoolean(); surface.Fingerprints[i] = r.ReadString(); }
            for (var i = 0; i < surface.Heights.Length; i++) { surface.Heights[i] = r.ReadUInt16(); surface.Valid[i] = r.ReadBoolean(); surface.Codes[i] = codes[r.ReadInt32()]; surface.EntityKeys[i] = codes[r.ReadInt32()]; surface.SepiaKeys[i] = codes[r.ReadInt32()]; surface.Water[i] = r.ReadBoolean(); }
            return data.Position == data.Length ? surface : null;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or IndexOutOfRangeException or ArgumentException) { return null; }
    }
}
