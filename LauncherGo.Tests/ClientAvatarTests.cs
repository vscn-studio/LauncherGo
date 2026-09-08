using System.Buffers.Binary;
using System.IO.Compression;
using ServerMap.Render;
using ServerMap.Web;
using Xunit;

namespace LauncherGo.Tests;

public sealed class ClientAvatarTests : IDisposable
{
    [Fact] public void HundredsOfFaceRectanglesPackWithoutExceedingTransferTextureLimit()
    {
        var sources = Enumerable.Range(0, 226).Select(i => new AvatarScene.Texture(8, 8,
            Enumerable.Range(0, 64).SelectMany(_ => new byte[] { (byte)i, 90, 170, 255 }).ToArray())).ToArray();
        var vertices = Enumerable.Range(0, sources.Length).SelectMany(i => new[] {
            new AvatarScene.Vertex(0, 0, 0, .5f, .5f, i), new AvatarScene.Vertex(0, 0, 1, .5f, .5f, i), new AvatarScene.Vertex(0, 1, 0, .5f, .5f, i) }).ToArray();
        var packed = AvatarTexturePacking.Pack(sources, vertices);
        Assert.Single(packed.Textures);
        Assert.True(packed.Textures.Sum(t => t.Width * t.Height) <= AvatarScene.MaxPixels);
        for (var i = 0; i < sources.Length; i++)
        {
            var vertex = packed.Vertices[i * 3]; var texture = packed.Textures[vertex.Texture];
            var offset = ((int)(vertex.V * texture.Height) * texture.Width + (int)(vertex.U * texture.Width)) * 4;
            Assert.Equal((byte)i, texture.Rgba[offset]);
            Assert.Equal(255, texture.Rgba[offset + 3]);
        }
        Assert.NotEmpty(AvatarScene.Unpack(packed.Pack()).Render());
    }
    [Fact] public void PackedTexturesKeepPixelAndDimensionBudgets()
    {
        Assert.Throws<InvalidDataException>(() => AvatarTexturePacking.Pack([new(513, 1, new byte[513 * 4])], Scene().Vertices));
        Assert.Throws<InvalidDataException>(() => AvatarTexturePacking.Pack(Enumerable.Repeat(new AvatarScene.Texture(512,512,new byte[512*512*4]),3).ToArray(), Scene().Vertices));
        var pages = AvatarTexturePacking.Pack([new(512,512,new byte[512*512*4]),new(512,512,new byte[512*512*4])], Scene().Vertices);
        Assert.Equal(2, pages.Textures.Length);
    }
    [Fact] public void ClientCaptureFailureIsTokenBoundAndVisibleUntilRecovery()
    {
        using var store = new ClientAvatarStore(root, _ => { });
        var token = store.Request("alice", "look", 1000)!;
        Assert.False(store.ReportFailure("bob", token, "capture-failed", 1001));
        Assert.False(store.ReportFailure("alice", "wrong", "capture-failed", 1001));
        Assert.False(store.ReportFailure("alice", token, "untrusted error text", 1001));
        Assert.True(store.ReportFailure("alice", token, "capture-failed", 1001));
        Assert.Equal("capture-failed", store.GetStatus("alice", "look"));
        Assert.Null(store.Request("alice", "look", 1002));
        token = store.Request("alice", "look", 121001)!;
        Assert.NotNull(token);
        Assert.Equal("capture-failed", store.GetStatus("alice", "look"));
        store.ForgetConnection("alice");
        Assert.Equal("waiting-client", store.GetStatus("alice", "look"));
    }
    private readonly string root = Path.Combine(Path.GetTempPath(), "launchergo-client-avatar-" + Guid.NewGuid().ToString("N"));
    private static AvatarScene Scene(byte r = 180) => new()
    {
        Textures = [new(1, 1, [r, 120, 90, 255])],
        Vertices = [new(0, -1, -1, 0, 0, 0), new(0, -1, 1, 1, 0, 0), new(0, 1, 1, 1, 1, 0), new(0, -1, -1, 0, 0, 0), new(0, 1, 1, 1, 1, 0), new(0, 1, -1, 0, 1, 0)]
    };
    [Fact] public void PortraitFacesNegativeXWithoutSideViewTiltOrMirroring()
    {
        var front = Scene().Vertices.Select(v => v with { X = -1 }).ToArray();
        var back = front.Select(v => v with { X = 1, Texture = 1 }).ToArray();
        var side = front.Select(v => v with { X = v.Z, Z = -1, Texture = 2 }).ToArray();
        var scene = new AvatarScene { Textures = [new(2, 1, [255,0,0,255, 255,255,0,255]), new(1,1,[0,0,255,255]), new(1,1,[0,255,0,255])], Vertices = [..back, ..side, ..front] };
        var png = scene.Render();
        using var compressed = new MemoryStream();
        for (var offset = 8; offset + 12 <= png.Length;)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(offset,4));
            if (System.Text.Encoding.ASCII.GetString(png, offset+4, 4) == "IDAT") compressed.Write(png, offset+8, length);
            offset += length + 12;
        }
        compressed.Position = 0;
        using var zlib = new ZLibStream(compressed, CompressionMode.Decompress);
        var pixels = new byte[256 * (256 * 4 + 1)]; zlib.ReadExactly(pixels);
        int Pixel(int x, int y) => y * (256 * 4 + 1) + 1 + x * 4;
        Assert.Equal(0, pixels[Pixel(60,128)+1]); // Low Z stays on the left (red).
        Assert.True(pixels[Pixel(195,128)+1] > 0); // High Z stays on the right (yellow).
        for (var y = 0; y < 256; y++) for (var x = 0; x < 256; x++)
        {
            var p = Pixel(x,y);
            if (pixels[p+3] == 0) continue;
            Assert.True(pixels[p] > 0); Assert.Equal(0, pixels[p+2]); // No green side or blue back.
        }
        foreach (var (x,y) in new[] { (20,20), (235,20), (20,235), (235,235) }) Assert.Equal(255, pixels[Pixel(x,y)+3]);
    }
    [Fact] public void FrontPortraitInvalidatesPreviousSidePortraitCacheKey()
    {
        var skin = new byte[] {1,2,3};
        var old = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("client-head-v2/alice/").Concat(skin).ToArray()));
        Assert.NotEqual(old, ClientAvatarStore.AppearanceKey("alice", skin));
    }
    [Fact] public void SceneRoundTripRendersDeterministicBoundedPng()
    {
        var scene = AvatarScene.Unpack(Scene().Pack()); var png = scene.Render();
        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, png[..8]);
        Assert.Equal(256, BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16, 4)));
        Assert.Equal(256, BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20, 4)));
        Assert.Equal(png, scene.Render()); Assert.NotEqual(png, Scene(40).Render());
    }
    [Fact] public void SceneRejectsInvalidVerticesTexturesAndOversizedInflation()
    {
        var bad = new AvatarScene { Textures = Scene().Textures, Vertices = [new(float.NaN, 0, 0, 0, 0, 0), new(0, 0, 0, 0, 0, 0), new(0, 0, 0, 0, 0, 0)] };
        Assert.Throws<InvalidDataException>(() => bad.Render());
        Assert.Throws<InvalidDataException>(() => new AvatarScene { Textures = [new(513, 1, [])], Vertices = Scene().Vertices }.Pack());
        Assert.Throws<InvalidDataException>(() => new AvatarScene { Textures = Scene().Textures, Vertices = Scene().Vertices.Select(v => v with { Texture = 1 }).ToArray() }.Pack());
        using var packed = new MemoryStream(); using (var gzip = new GZipStream(packed, CompressionLevel.Fastest, true)) gzip.Write(new byte[AvatarScene.MaxBytes + 1]);
        Assert.Throws<InvalidDataException>(() => AvatarScene.Unpack(packed.ToArray()));
    }
    [Fact] public void RenderHonorsCancellationAndRejectsInvisibleHead()
    {
        using var stop = new CancellationTokenSource(); stop.Cancel(); Assert.Throws<OperationCanceledException>(() => Scene().Render(stop.Token));
        Assert.Throws<InvalidDataException>(() => new AvatarScene { Textures = [new(1, 1, [0, 0, 0, 0])], Vertices = Scene().Vertices }.Render());
    }
    [Fact] public async Task TransfersAreSolicitedSenderBoundPersistedAndContentAddressed()
    {
        string? key;
        using (var store = new ClientAvatarStore(root, _ => { }))
        {
            var token = store.Request("alice", "look", 1000)!; var data = Scene().Pack();
            Assert.Equal("waiting-model", store.GetStatus("alice", "look"));
            Assert.Equal("waiting-client", store.GetStatus("bob", "look"));
            Assert.Equal("waiting-appearance", store.GetStatus("bob", null));
            Assert.NotNull(token); Assert.Null(store.Request("alice", "look", 1001));
            Assert.False(store.Receive("bob", token, 0, 1, data, 1002));
            Assert.False(store.Receive("alice", "wrong", 0, 1, data, 1002));
            Assert.True(store.Receive("alice", token, 0, 1, data, 1002));
            for (var i = 0; i < 200 && store.GetKey("alice", "look") == null; i++) await Task.Delay(10);
            key = store.GetKey("alice", "look"); Assert.NotNull(key); Assert.NotNull(store.Get(key!));
            Assert.Equal("ready", store.GetStatus("alice", "look"));
            Assert.Null(store.GetKey("bob", "look")); Assert.Null(store.GetKey("alice", "changed")); Assert.Null(store.Get("../../secret"));
            Assert.False(store.Receive("alice", token, 0, 1, data, 1003));
        }
        using var restored = new ClientAvatarStore(root, _ => { }); Assert.Equal(key, restored.GetKey("alice", "look")); Assert.Null(restored.Request("alice", "look", 1_000_000));
        Assert.NotNull(restored.Request("alice", "look", 1_000_000, refresh:true)); Assert.Equal(key, restored.GetKey("alice", "look"));
    }
    [Fact] public void BadOrderExpiredTokensAndMemoryFloodAreRejected()
    {
        using var store = new ClientAvatarStore(root, _ => { }); var token = store.Request("a", "look", 0)!;
        Assert.False(store.Receive("a", token, 1, 2, [1], 1)); Assert.Null(store.Request("a", "look", 2));
        token = store.Request("a", "look", 120001)!; Assert.False(store.Receive("a", token, 0, 1, [1], 210002));
        for (var i = 0; i < 8; i++) Assert.NotNull(store.Request("p" + i, "look", 220000));
        Assert.Null(store.Request("overflow", "look", 220000));
        store.ForgetConnection("p0"); Assert.NotNull(store.Request("overflow", "look", 220001));
    }
    [Fact] public void InGameFlagDefaultsOffAndPersistsIndependentlyOfWebHiding()
    {
        var path = Path.Combine(root, "notebook.json"); var store = new MapNotebookStore(path);
        var region = store.SaveRegion(null, "Private", 0, 0, 20, 20); Assert.False(region.HideInGame);
        Assert.False(MapVisibility.Visible(store.Regions, 10, 10));
        store.SaveRegion(region.Id, region.Name, 0, 0, 20, 20, true);
        Assert.True(new MapNotebookStore(path).Regions.Single().HideInGame);
        store.SaveRegion(region.Id, region.Name, 0, 0, 20, 20, false);
        Assert.False(new MapNotebookStore(path).Regions.Single().HideInGame); Assert.False(MapVisibility.Visible(store.Regions, 10, 10));
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
