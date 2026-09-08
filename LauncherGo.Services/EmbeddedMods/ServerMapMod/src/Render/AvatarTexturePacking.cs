namespace ServerMap.Render;

// Many faces can use distinct small UV rectangles. Pack those rectangles into
// bounded pages instead of counting each face as a separate transfer texture.
public static class AvatarTexturePacking
{
    private sealed class Row(int y, int height) { public int Y = y, Height = height, X; }
    private sealed class Page { public List<Row> Rows = []; public int Width, Height; }
    private sealed record Placement(int Page, int X, int Y);

    public static AvatarScene Pack(IReadOnlyList<AvatarScene.Texture> sources, IReadOnlyList<AvatarScene.Vertex> vertices)
    {
        if (sources.Count is < 1 or > AvatarScene.MaxVertices || vertices.Count > AvatarScene.MaxVertices ||
            sources.Sum(t => (long)t.Width * t.Height) > AvatarScene.MaxPixels)
            throw new InvalidDataException("Head texture pixel budget exceeded");
        var pages = new List<Page>();
        var placements = new Placement[sources.Count];
        foreach (var index in Enumerable.Range(0, sources.Count).OrderByDescending(i => sources[i].Height).ThenByDescending(i => sources[i].Width))
        {
            var source = sources[index];
            if (source.Width is < 1 or > 512 || source.Height is < 1 or > 512 || source.Rgba.Length != source.Width * source.Height * 4)
                throw new InvalidDataException("Head texture dimensions exceed 512 pixels");
            for (var pageIndex = 0; ; pageIndex++)
            {
                if (pageIndex == pages.Count) pages.Add(new Page());
                var page = pages[pageIndex];
                var row = page.Rows.FirstOrDefault(r => r.Height >= source.Height && r.X + source.Width <= 512);
                if (row == null)
                {
                    if (page.Height + source.Height > 512) continue;
                    row = new Row(page.Height, source.Height); page.Rows.Add(row); page.Height += source.Height;
                }
                placements[index] = new(pageIndex, row.X, row.Y);
                row.X += source.Width; page.Width = Math.Max(page.Width, row.X);
                break;
            }
        }
        if (pages.Count > 128 || pages.Sum(p => (long)p.Width * p.Height) > AvatarScene.MaxPixels)
            throw new InvalidDataException("Packed head textures exceed pixel budget");
        var packed = pages.Select(p => new AvatarScene.Texture(p.Width, p.Height, new byte[p.Width * p.Height * 4])).ToArray();
        for (var i = 0; i < sources.Count; i++)
        {
            var source = sources[i]; var placement = placements[i]; var target = packed[placement.Page];
            for (var y = 0; y < source.Height; y++)
                source.Rgba.AsSpan(y * source.Width * 4, source.Width * 4).CopyTo(target.Rgba.AsSpan(((placement.Y + y) * target.Width + placement.X) * 4));
        }
        var remapped = vertices.Select(v =>
        {
            if (v.Texture < 0 || v.Texture >= sources.Count) throw new InvalidDataException("Invalid head texture index");
            var source = sources[v.Texture]; var at = placements[v.Texture]; var target = packed[at.Page];
            return v with { Texture = at.Page,
                U = (at.X + Math.Clamp(v.U * source.Width, .5f, source.Width - .5f)) / target.Width,
                V = (at.Y + Math.Clamp(v.V * source.Height, .5f, source.Height - .5f)) / target.Height };
        }).ToArray();
        var scene = new AvatarScene { Textures = packed, Vertices = remapped }; scene.Validate(); return scene;
    }
}
