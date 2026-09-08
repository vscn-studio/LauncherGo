using ServerMap.Web;
using Xunit;

namespace LauncherGo.Tests;

public sealed class LocalAvatarTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "launchergo-avatar-test-" + Guid.NewGuid().ToString("N"));
    private static LocalAvatarRenderer.Appearance Look => new("skin2", "amethyst", "bald", "none", "none", "none", "lightgray");
    [Fact]
    public void CompositingRespectsBlackWhiteAlphaAndTranslucentMasks()
    {
        byte[] background = [0,0,255,255, 0,0,255,255, 0,0,255,255, 0,0,0,0];
        byte[] overlay = [255,0,0,255, 255,0,0,255, 255,0,0,255, 255,0,0,128];
        byte[] mask = [0,0,0,255, 255,255,255,255, 255,255,255,128, 255,255,255,255];
        LocalAvatarRenderer.Composite(background, overlay, mask);
        Assert.Equal(new byte[] {0,0,255,255, 255,0,0,255, 128,0,127,255, 255,0,0,128}, background);
    }
    [Fact]
    public void KeysTrackAppearanceAndAssetRevisionAndRejectPaths()
    {
        Assert.True(Look.Valid);
        Assert.NotEqual(Look.Key("a"), Look.Key("b"));
        Assert.NotEqual(Look.Key("a"), (Look with { Beard = "full" }).Key("a"));
        foreach (var value in new[] {"../skin2", "a/b", "a\\b", "", "<script>"}) Assert.False((Look with {BaseSkin=value}).Valid);
    }
    private LocalAvatarRenderer Renderer(Action? decoded = null)
    {
        foreach (var file in new[] {"baseskin/skin2.png", "eyecolor/amethyst.png", "hairbase/bald/lightgray.png"})
        {
            var path = Path.Combine(root, "layers", file); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllBytes(path, [1]);
        }
        return new LocalAvatarRenderer(Path.Combine(root, "layers"), _ =>
        {
            decoded?.Invoke(); var pixels = new byte[256*256*4];
            for (var i=0;i<pixels.Length;i+=4) { pixels[i]=42; pixels[i+3]=255; }
            return pixels;
        });
    }
    [Fact]
    public void MissingVariantsFailInsteadOfReturningAnIncorrectAppearance()
    {
        var renderer = Renderer();
        Assert.Throws<FileNotFoundException>(() => renderer.Render(Look with {Beard="unknown"}));
        Assert.Throws<ArgumentException>(() => renderer.Render(Look with {HairColor="../secret"}));
        Assert.Equal(new byte[] {137,80,78,71,13,10,26,10}, renderer.Render(Look)[..8]);
    }
    [Fact]
    public async Task RepeatedRequestsGenerateOnceAndReusePersistentCache()
    {
        var count=0;var renderer=Renderer(()=>Interlocked.Increment(ref count));var path=Path.Combine(root,"cache");
        using (var cache = new LocalAvatarCache(renderer,path,"revision",_=>{}))
        {
            for(var i=0;i<20;i++) cache.Request(Look);
            var key=await WaitFor(cache);Assert.Equal(3,count);Assert.NotNull(cache.Get(key));
            var copy=cache.Get(key)!;copy[0]=0;Assert.Equal(137,cache.Get(key)![0]);
            Assert.Null(cache.Get("../secret"));
        }
        using var restored=new LocalAvatarCache(renderer,path,"revision",_=>{});
        await WaitFor(restored);Assert.Equal(3,count);
        static async Task<string> WaitFor(LocalAvatarCache cache)
        {
            for(var i=0;i<200;i++){var key=cache.Request(Look);if(key!=null)return key;await Task.Delay(10);}
            throw new TimeoutException("Avatar did not finish");
        }
    }
    public void Dispose() { if(Directory.Exists(root))Directory.Delete(root,true); }
}
