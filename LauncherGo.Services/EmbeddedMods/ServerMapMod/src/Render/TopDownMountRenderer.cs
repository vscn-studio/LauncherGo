using System.Numerics;

namespace ServerMap.Render;

/// <summary>16 orthographic, shallow-oblique views in one atlas. Camera and light stay fixed in map space.</summary>
public static class TopDownMountRenderer
{
    public const int FrameSize = 256, Columns = 4, Directions = 16, Size = FrameSize * Columns;
    public const int MaxVertices = 72000, MaxPixels = 2 * 1024 * 1024;
    public const double TiltDegrees = 25;
    private static readonly float TiltCos = (float)Math.Cos(TiltDegrees * Math.PI / 180), TiltSin = (float)Math.Sin(TiltDegrees * Math.PI / 180);
    private static readonly Vector3 Light = Vector3.Normalize(new(-.4f, 1, -.6f)), Camera = new(0, TiltCos, TiltSin);
    public sealed record Result(byte[] Png, double WorldSize, double CenterX, double CenterZ, double Footprint);
    // Same zero-yaw model basis as the game. Positive yaw turns +X toward -Z.
    private static Vector3 Rotate(AvatarScene.Vertex v, int direction)
    {
        var angle = direction * Math.PI * 2 / Directions; var c = (float)Math.Cos(angle); var s = (float)Math.Sin(angle);
        return new(c * v.X + s * v.Z, v.Y, -s * v.X + c * v.Z);
    }
    private static Vector3 Project(Vector3 v) => new(v.X, v.Z * TiltCos - v.Y * TiltSin, -(v.Y * TiltCos + v.Z * TiltSin));
    public static Result Render(AvatarScene scene, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        scene.Validate(MaxVertices, MaxPixels);
        var vs = scene.Vertices;
        var footprint = Math.Max(vs.Max(v => v.X) - vs.Min(v => v.X), vs.Max(v => v.Z) - vs.Min(v => v.Z));
        if (footprint < .01 || footprint > 192) throw new InvalidDataException("Mount footprint out of bounds");
        var minX = float.PositiveInfinity; var maxX = float.NegativeInfinity; var minY = float.PositiveInfinity; var maxY = float.NegativeInfinity;
        // One projection scale and one entity-origin anchor across all headings: no size pulsing or recentering.
        for (var direction = 0; direction < Directions; direction++)
        {
            token.ThrowIfCancellationRequested();
            foreach (var v in vs) { var p = Project(Rotate(v, direction)); minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X); minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y); }
        }
        var worldSize = Math.Max(maxX - minX, maxY - minY) * FrameSize / (FrameSize - 8d);
        if (worldSize > 320) throw new InvalidDataException("Mount projected bounds too large");
        var cx = (minX + maxX) / 2d; var cz = (minY + maxY) / 2d; var scale = FrameSize / worldSize;
        var rgba = new byte[Size * Size * 4]; long work = 0; var visible = false;
        static float Edge(Vector3 a, Vector3 b, float x, float y) => (x - a.X) * (b.Y - a.Y) - (y - a.Y) * (b.X - a.X);
        for (var direction = 0; direction < Directions; direction++)
        {
            var world = vs.Select(v => Rotate(v, direction)).ToArray();
            var points = world.Select(v => { var p = Project(v); return new Vector3((float)((p.X - cx) * scale + FrameSize / 2d), (float)((p.Y - cz) * scale + FrameSize / 2d), p.Z); }).ToArray();
            var depth = Enumerable.Repeat(float.PositiveInfinity, FrameSize * FrameSize).ToArray();
            var atlasX = direction % Columns * FrameSize; var atlasY = direction / Columns * FrameSize;
            for (var i = 0; i < points.Length; i += 3)
            {
                token.ThrowIfCancellationRequested(); var a = points[i]; var b = points[i + 1]; var c = points[i + 2]; var area = Edge(a, b, c.X, c.Y);
                if (Math.Abs(area) < .00001) continue;
                var x0 = Math.Clamp((int)Math.Floor(Math.Min(a.X, Math.Min(b.X, c.X))), 0, FrameSize - 1); var x1 = Math.Clamp((int)Math.Ceiling(Math.Max(a.X, Math.Max(b.X, c.X))), 0, FrameSize - 1);
                var y0 = Math.Clamp((int)Math.Floor(Math.Min(a.Y, Math.Min(b.Y, c.Y))), 0, FrameSize - 1); var y1 = Math.Clamp((int)Math.Ceiling(Math.Max(a.Y, Math.Max(b.Y, c.Y))), 0, FrameSize - 1);
                work += (long)(x1 - x0 + 1) * (y1 - y0 + 1); if (work > 128_000_000) throw new InvalidDataException("Mount raster budget exceeded");
                var va = vs[i]; var vb = vs[i + 1]; var vc = vs[i + 2]; var texture = scene.Textures[va.Texture];
                var normal = Vector3.Cross(world[i + 1] - world[i], world[i + 2] - world[i]);
                if (Vector3.Dot(normal, Camera) < 0) normal = -normal; // Native meshes can contain double-sided faces.
                var light = normal.LengthSquared() > 0 ? .6f + .4f * Math.Max(0, Vector3.Dot(Vector3.Normalize(normal), Light)) : 1;
                for (var y = y0; y <= y1; y++)
                {
                    token.ThrowIfCancellationRequested();
                    for (var x = x0; x <= x1; x++)
                    {
                        var w0 = Edge(b, c, x + .5f, y + .5f) / area; var w1 = Edge(c, a, x + .5f, y + .5f) / area; var w2 = 1 - w0 - w1;
                        if (w0 < -.00001 || w1 < -.00001 || w2 < -.00001) continue;
                        var z = w0 * a.Z + w1 * b.Z + w2 * c.Z; var pixel = y * FrameSize + x; if (z >= depth[pixel]) continue;
                        var u = Math.Clamp((int)((w0 * va.U + w1 * vb.U + w2 * vc.U) * texture.Width), 0, texture.Width - 1);
                        var v = Math.Clamp((int)((w0 * va.V + w1 * vb.V + w2 * vc.V) * texture.Height), 0, texture.Height - 1); var source = (v * texture.Width + u) * 4;
                        if (texture.Rgba[source + 3] < 128) continue;
                        depth[pixel] = z; var dest = ((atlasY + y) * Size + atlasX + x) * 4;
                        for (var ch = 0; ch < 3; ch++) rgba[dest + ch] = (byte)(texture.Rgba[source + ch] * light); rgba[dest + 3] = 255; visible = true;
                    }
                }
            }
        }
        if (!visible) throw new InvalidDataException("Mount has no visible surface");
        return new(PngEncoder.Encode(Size, Size, rgba), worldSize, cx, cz, footprint);
    }
}
