using System.IO.Compression;
using System.Numerics;

namespace ServerMap.Render;

/// <summary>Bounded head-only mesh and cropped RGBA textures, never executable model files.</summary>
public sealed class AvatarScene
{
    public const int MaxBytes = 3 * 1024 * 1024, MaxVertices = 12000, MaxPixels = 512 * 1024;
    public sealed record Texture(int Width, int Height, byte[] Rgba);
    public sealed record Vertex(float X, float Y, float Z, float U, float V, int Texture);
    public Texture[] Textures { get; init; } = [];
    public Vertex[] Vertices { get; init; } = [];

    public byte[] Pack()
    {
        Validate();
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, true))
        using (var writer = new BinaryWriter(gzip))
        {
            writer.Write(1); writer.Write(Textures.Length);
            foreach (var t in Textures) { writer.Write(t.Width); writer.Write(t.Height); writer.Write(t.Rgba); }
            writer.Write(Vertices.Length);
            foreach (var v in Vertices) { writer.Write(v.X); writer.Write(v.Y); writer.Write(v.Z); writer.Write(v.U); writer.Write(v.V); writer.Write(v.Texture); }
        }
        var bytes = output.ToArray();
        if (bytes.Length > MaxBytes) throw new InvalidDataException("Avatar transfer too large");
        return bytes;
    }
    public static AvatarScene Unpack(byte[] bytes)
    {
        if (bytes.Length is < 1 or > MaxBytes) throw new InvalidDataException("Avatar transfer size");
        using var input = new MemoryStream(bytes);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        var buffer = new byte[16384]; int count;
        while ((count = gzip.Read(buffer)) > 0)
        {
            if (output.Length + count > MaxBytes) throw new InvalidDataException("Avatar decompression limit");
            output.Write(buffer, 0, count);
        }
        output.Position = 0; using var reader = new BinaryReader(output);
        if (reader.ReadInt32() != 1) throw new InvalidDataException("Avatar format");
        var textures = new Texture[Bound(reader.ReadInt32(), 1, 128)]; var pixels = 0;
        for (var i = 0; i < textures.Length; i++)
        {
            var w = Bound(reader.ReadInt32(), 1, 512); var h = Bound(reader.ReadInt32(), 1, 512);
            pixels += w * h; if (pixels > MaxPixels) throw new InvalidDataException("Avatar pixel limit");
            var rgba = reader.ReadBytes(w * h * 4); if (rgba.Length != w * h * 4) throw new EndOfStreamException();
            textures[i] = new(w, h, rgba);
        }
        var vertices = new Vertex[Bound(reader.ReadInt32(), 3, MaxVertices)];
        for (var i = 0; i < vertices.Length; i++) vertices[i] = new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadInt32());
        if (output.Position != output.Length) throw new InvalidDataException("Trailing avatar data");
        var scene = new AvatarScene { Textures = textures, Vertices = vertices }; scene.Validate(); return scene;
    }
    private static int Bound(int n, int min, int max) => n >= min && n <= max ? n : throw new InvalidDataException("Avatar bounds");
    public void Validate()
    {
        Bound(Textures.Length, 1, 128); Bound(Vertices.Length, 3, MaxVertices);
        if (Vertices.Length % 3 != 0 || Textures.Sum(t => (long)t.Width * t.Height) > MaxPixels) throw new InvalidDataException("Avatar limits");
        foreach (var t in Textures) { Bound(t.Width, 1, 512); Bound(t.Height, 1, 512); if (t.Rgba.Length != t.Width * t.Height * 4) throw new InvalidDataException("Avatar pixels"); }
        foreach (var v in Vertices)
            if (!float.IsFinite(v.X + v.Y + v.Z + v.U + v.V) || Math.Max(Math.Abs(v.X), Math.Max(Math.Abs(v.Y), Math.Abs(v.Z))) > 100 || v.U < 0 || v.U > 1 || v.V < 0 || v.V > 1 || v.Texture < 0 || v.Texture >= Textures.Length)
                throw new InvalidDataException("Avatar vertex");
        for (var i = 0; i < Vertices.Length; i += 3)
            if (Vertices[i].Texture != Vertices[i + 1].Texture || Vertices[i].Texture != Vertices[i + 2].Texture) throw new InvalidDataException("Avatar triangle textures");
    }

    /// <summary>Front-facing orthographic portrait with bounded software rasterization.</summary>
    public byte[] Render(CancellationToken token = default)
    {
        Validate(); const int size = 256;
        // The native Seraph face points toward -X (eyes sit on the west face).
        // Look along +X: screen-right is +Z, up is +Y, nearer depth is smaller X.
        // No extra yaw/pitch, and no horizontal mirror of the player's face.
        var points = Vertices.Select(v => new Vector3(v.Z, v.Y, v.X)).ToArray();
        var minX = points.Min(p => p.X); var maxX = points.Max(p => p.X); var minY = points.Min(p => p.Y); var maxY = points.Max(p => p.Y);
        var extent = Math.Max(maxX - minX, maxY - minY); if (extent < .00001) throw new InvalidDataException("Empty avatar geometry");
        var scale = 228 / extent; var cx = (minX + maxX) / 2; var cy = (minY + maxY) / 2;
        for (var i = 0; i < points.Length; i++) points[i] = new((points[i].X - cx) * scale + 128, 128 - (points[i].Y - cy) * scale, points[i].Z);
        var rgba = new byte[size * size * 4]; var depth = Enumerable.Repeat(float.PositiveInfinity, size * size).ToArray(); long work = 0;
        static float Edge(Vector3 a, Vector3 b, float x, float y) => (x - a.X) * (b.Y - a.Y) - (y - a.Y) * (b.X - a.X);
        for (var i = 0; i < points.Length; i += 3)
        {
            token.ThrowIfCancellationRequested(); var a = points[i]; var b = points[i + 1]; var c = points[i + 2]; var area = Edge(a, b, c.X, c.Y);
            if (Math.Abs(area) < .00001) continue;
            var x0 = Math.Clamp((int)Math.Floor(Math.Min(a.X, Math.Min(b.X, c.X))), 0, 255); var x1 = Math.Clamp((int)Math.Ceiling(Math.Max(a.X, Math.Max(b.X, c.X))), 0, 255);
            var y0 = Math.Clamp((int)Math.Floor(Math.Min(a.Y, Math.Min(b.Y, c.Y))), 0, 255); var y1 = Math.Clamp((int)Math.Ceiling(Math.Max(a.Y, Math.Max(b.Y, c.Y))), 0, 255);
            work += (x1 - x0 + 1) * (y1 - y0 + 1); if (work > 12_000_000) throw new InvalidDataException("Avatar raster budget");
            var va = Vertices[i]; var vb = Vertices[i + 1]; var vc = Vertices[i + 2]; var texture = Textures[va.Texture];
            var normal = Vector3.Cross(new(vb.X - va.X, vb.Y - va.Y, vb.Z - va.Z), new(vc.X - va.X, vc.Y - va.Y, vc.Z - va.Z));
            var light = normal.LengthSquared() > 0 ? .68f + .32f * Math.Abs(Vector3.Dot(Vector3.Normalize(normal), Vector3.Normalize(new(-.35f, .5f, -.8f)))) : 1;
            for (var y = y0; y <= y1; y++) for (var x = x0; x <= x1; x++)
            {
                var w0 = Edge(b, c, x + .5f, y + .5f) / area; var w1 = Edge(c, a, x + .5f, y + .5f) / area; var w2 = 1 - w0 - w1;
                if (w0 < -.00001 || w1 < -.00001 || w2 < -.00001) continue;
                var z = w0 * a.Z + w1 * b.Z + w2 * c.Z; var pixel = y * size + x; if (z >= depth[pixel]) continue;
                var u = Math.Clamp((int)((w0 * va.U + w1 * vb.U + w2 * vc.U) * texture.Width), 0, texture.Width - 1);
                var v = Math.Clamp((int)((w0 * va.V + w1 * vb.V + w2 * vc.V) * texture.Height), 0, texture.Height - 1); var source = (v * texture.Width + u) * 4;
                if (texture.Rgba[source + 3] < 128) continue;
                depth[pixel] = z; for (var ch = 0; ch < 3; ch++) rgba[pixel * 4 + ch] = (byte)(texture.Rgba[source + ch] * light); rgba[pixel * 4 + 3] = 255;
            }
        }
        if (!rgba.Where((_, i) => i % 4 == 3).Any(a => a > 0)) throw new InvalidDataException("Avatar has no visible pixels");
        return PngEncoder.Encode(size, size, rgba);
    }
}
