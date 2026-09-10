using ServerMap.Render;
using ServerMap.Web;
using Xunit;
using System.Buffers.Binary;
using System.IO.Compression;

namespace LauncherGo.Tests;

public class MountMapTests
{
    private static byte[] DecodeAtlas(byte[] png)
    {
        Assert.Equal(1024, BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16,4)));
        Assert.Equal(1024, BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20,4)));
        using var data=new MemoryStream();
        for(var offset=8;offset+12<=png.Length;)
        {
            var length=BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(offset,4));
            if(png.AsSpan(offset+4,4).SequenceEqual("IDAT"u8))data.Write(png.AsSpan(offset+8,length));
            offset+=length+12;
        }
        data.Position=0;using var zlib=new ZLibStream(data,CompressionMode.Decompress);
        var raw=new byte[1024*(4096+1)];zlib.ReadExactly(raw);var rgba=new byte[1024*4096];
        for(var y=0;y<1024;y++){Assert.Equal(0,raw[y*4097]);raw.AsSpan(y*4097+1,4096).CopyTo(rgba.AsSpan(y*4096));}
        return rgba;
    }
    private static AvatarScene.Vertex[] Quad(float x, float y, float z, float width, float length, int texture) =>
        [new(x,y,z,0,0,texture),new(x+width,y,z,1,0,texture),new(x,y,z+length,0,1,texture),
         new(x+width,y,z,1,0,texture),new(x+width,y,z+length,1,1,texture),new(x,y,z+length,0,1,texture)];
    private static AvatarScene Scene(params AvatarScene.Vertex[] vertices) => new()
    {
        Textures = [new(1,1,[255,0,0,255]),new(1,1,[0,255,0,255]),new(1,1,[0,0,255,0])], Vertices = vertices
    };
    private static byte[] Pixel(byte[] rgba, int frame, int x, int y) => rgba.AsSpan(((frame / 4 * 256 + y) * 1024 + frame % 4 * 256 + x) * 4, 4).ToArray();
    private static byte[] ProjectedPixel(TopDownMountRenderer.Result render, byte[] rgba, int frame, double x, double y, double z)
    {
        var angle = frame * Math.PI / 8; var rx = Math.Cos(angle) * x + Math.Sin(angle) * z;
        var rz = -Math.Sin(angle) * x + Math.Cos(angle) * z;
        var sy = rz * Math.Cos(25 * Math.PI / 180) - y * Math.Sin(25 * Math.PI / 180);
        return Pixel(rgba, frame, (int)((rx-render.CenterX)/render.WorldSize*256+128), (int)((sy-render.CenterZ)/render.WorldSize*256+128));
    }

    [Fact]
    public void ProjectionKeepsWorldFootprintOffsetsAndTopSurface()
    {
        var render = TopDownMountRenderer.Render(Scene([..Quad(-2,0,-1,4,2,0), ..Quad(-1,2,-.5f,2,1,1), ..Quad(-2,3,-1,4,2,2)]));
        Assert.Equal(4, render.Footprint); Assert.Equal(0, render.CenterX, 5);
        Assert.True(render.CenterZ < 0); Assert.InRange(render.WorldSize, 4.1, 6);
        var rgba = DecodeAtlas(render.Png);
        Assert.Equal(1024*1024*4,rgba.Length);
        for(var frame=0;frame<16;frame++)
        {
            Assert.Equal(0,Pixel(rgba,frame,0,128)[3]); Assert.Equal(0,Pixel(rgba,frame,128,0)[3]);
            Assert.True(ProjectedPixel(render,rgba,frame,0,2,0)[1]>200, "Highest visible surface remains green; transparent upper surface does not occlude");
        }
        Assert.True(ProjectedPixel(render,rgba,0,1.8,0,0)[0]>200);
    }

    [Fact]
    public void RendererRejectsUnboundedDegenerateAndCancelledScenes()
    {
        Assert.Throws<InvalidDataException>(() => TopDownMountRenderer.Render(Scene(Quad(0,0,0,0,0,0))));
        Assert.Throws<InvalidDataException>(() => TopDownMountRenderer.Render(Scene(Quad(0,0,0,201,1,0))));
        Assert.Throws<InvalidDataException>(() => TopDownMountRenderer.Render(Scene(Quad(0,0,0,1,1,2))));
        Assert.Throws<InvalidDataException>(() => TopDownMountRenderer.Render(Scene(Enumerable.Repeat(Quad(0,0,0,1,1,0), 12001).SelectMany(v=>v).ToArray())));
        Assert.Throws<InvalidDataException>(() => TopDownMountRenderer.Render(Scene(Enumerable.Repeat(Quad(0,0,0,1,1,0), 1000).SelectMany(v=>v).ToArray())));
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        Assert.Throws<OperationCanceledException>(() => TopDownMountRenderer.Render(Scene(Quad(0,0,0,1,1,0)), cancel.Token));
    }

    private static MountSnapshotStore.Mount Mount(long id = 1) => new(id,"game:boat", "Boat",10,100,20,0,[new("alice","Alice",10,20),new("bob","Bob",10,20)]);
    [Theory]
    [InlineData(1,2)] [InlineData(4,2)] [InlineData(6,1.5)] [InlineData(8,1)] [InlineData(100,1)]
    public void SizeBoostDependsOnPhysicalModelNotHeadingOrZoom(double footprint,double scale) => Assert.Equal(scale,MountSnapshotStore.DisplayScale(footprint));

    [Fact]
    public void ObliqueAtlasRevealsSidesAndChangesOcclusionInsteadOfRotatingPicture()
    {
        AvatarScene.Vertex[] wall = [new(-1,0,1,0,0,1),new(1,0,1,1,0,1),new(-1,2,1,0,1,1),new(1,0,1,1,0,1),new(1,2,1,1,1,1),new(-1,2,1,0,1,1)];
        var result=TopDownMountRenderer.Render(Scene([..Quad(-1,2,-1,2,2,0),..wall]));
        var rgba=DecodeAtlas(result.Png);
        var front=ProjectedPixel(result,rgba,0,0,1,1);var back=ProjectedPixel(result,rgba,8,0,1,1);
        Assert.True(front[1]>100&&front[0]==0,"Vertical front wall is visible from a shallow oblique camera");
        Assert.True(back[0]>200&&back[1]==0,"After turning, the top occludes the same wall");
        Assert.True(front[1]<back[0],"Fixed world light makes the side darker than the top");
        var output=Environment.GetEnvironmentVariable("MOUNT_TEST_ATLAS");if(!string.IsNullOrEmpty(output))File.WriteAllBytes(output,result.Png);
    }
    private static async Task WaitFor(Func<bool> check)
    {
        for (var i=0;i<200;i++) { if(check())return; await Task.Delay(10); }
        Assert.True(check(), "Snapshot worker did not finish");
    }
    [Fact]
    public async Task StoreRequiresSolicitedRiderOwnedTransferAndCachesOneImagePerVehicle()
    {
        using var store = new MountSnapshotStore(bytes => [..bytes,42]); var now=Environment.TickCount64;
        store.Replace([Mount()],now);
        Assert.Null(store.Request("outsider",1,now)); Assert.Null(store.Request("alice",2,now));
        var token=store.Request("alice",1,now)!; Assert.Equal(32,token.Length);
        Assert.Null(store.Request("bob",1,now));
        Assert.False(store.Receive("bob",1,token,0,1,[1],4,0,0,now,4));
        Assert.False(store.Receive("alice",1,"forged",0,1,[1],4,0,0,now,4));
        Assert.True(store.Receive("alice",1,token,0,2,[1,2],4,0,0,now,4));
        Assert.True(store.Receive("alice",1,token,1,2,[3,4],4,0,0,now,4));
        await WaitFor(()=>store.Get(1)!=null);
        Assert.Equal(new byte[]{1,2,3,4,42},store.Get(1)!.Png);
        Assert.Single(store.Active); Assert.Equal(2,store.Active[0].Riders.Length);
        Assert.False(store.Receive("alice",1,token,1,2,[3,4],4,0,0,now,4));
        Assert.Null(store.Request("bob",1,now+1000));
        Assert.NotNull(store.Request("bob",1,now+121000));
        store.Replace([],now+121001); Assert.Empty(store.Active);
    }

    [Theory]
    [InlineData(1,1,4,0,0,1)] // Out of sequence.
    [InlineData(0,87,4,0,0,1)]
    [InlineData(0,1,321,0,0,1)]
    [InlineData(0,1,4,double.NaN,0,1)]
    [InlineData(0,1,4,0,101,1)]
    [InlineData(0,1,4,0,0,49153)]
    public void StoreRejectsInvalidTransfer(int index,int total,double size,double x,double z,int bytes)
    {
        using var store=new MountSnapshotStore(b=>b);var now=Environment.TickCount64;store.Replace([Mount()],now);
        var token=store.Request("alice",1,now)!;
        Assert.False(store.Receive("alice",1,token,index,total,new byte[bytes],size,x,z,now,4));
        Assert.Null(store.Get(1)); Assert.Null(store.Request("alice",1,now+100));
    }

    [Fact]
    public async Task DismountWhileWorkerRunsCannotPublishAndExpiredRequestsAreRejected()
    {
        using var entered=new ManualResetEventSlim(); using var release=new ManualResetEventSlim();
        var exited=false;
        using var store=new MountSnapshotStore(bytes=>{entered.Set();release.Wait(TimeSpan.FromSeconds(3));Volatile.Write(ref exited,true);return bytes;});
        var now=Environment.TickCount64;store.Replace([Mount()],now);var token=store.Request("alice",1,now)!;
        Assert.False(store.Receive("alice",1,token,0,1,[1],4,0,0,now+60001,4));
        store.Replace([Mount()],now+61000);token=store.Request("alice",1,now+61000)!;
        Assert.True(store.Receive("alice",1,token,0,1,[1],4,0,0,now+61000,4));
        await WaitFor(()=>entered.IsSet);store.Replace([],now+61001);release.Set();
        await WaitFor(()=>Volatile.Read(ref exited));Assert.Null(store.Get(1));
    }

    [Fact]
    public void StoreRejectsChangedOrInvalidPhysicalSizeDuringUpload()
    {
        var now=Environment.TickCount64;
        foreach(var footprint in new[]{0,double.NaN,193})
        {
            using var bad=new MountSnapshotStore(b=>b);bad.Replace([Mount()],now);var token=bad.Request("alice",1,now)!;
            Assert.False(bad.Receive("alice",1,token,0,1,[1],4,0,0,now,footprint));
        }
        using var store=new MountSnapshotStore(b=>b);store.Replace([Mount()],now);var valid=store.Request("alice",1,now)!;
        Assert.True(store.Receive("alice",1,valid,0,2,[1],4,0,0,now,4));
        Assert.False(store.Receive("alice",1,valid,1,2,[2],4,0,0,now,8));Assert.Null(store.Get(1));
    }

    [Fact]
    public void ImagesAndFeaturesSharePlayerPermissionsAndRotatedFootprintPrivacy()
    {
        var mount=Mount() with {Yaw=(float)(Math.PI/2)};
        var image=new MountSnapshotStore.Image("key",[],4,0,-1,0,4);
        var b=MountVisibility.Bounds(mount,image);
        Assert.Equal(10,(b.MinX+b.MaxX)/2,5); Assert.Equal(18,(b.MinZ+b.MaxZ)/2,5);
        Assert.True(MountVisibility.Visible(mount,image,true,"alice",false,[]));
        Assert.True(MountVisibility.Visible(mount,image,true,"bob",false,[]));
        Assert.False(MountVisibility.Visible(mount,image,true,"other",false,[]));
        Assert.False(MountVisibility.Visible(mount,image,true,null,false,[]));
        Assert.False(MountVisibility.Visible(mount,image,false,"admin",true,[]));
        MapNotebookStore.Region[] regions=[new("r","hidden",9,16,10,17)]; // Entity origin remains visible.
        Assert.False(MountVisibility.Visible(mount,image,true,"alice",false,regions));
        Assert.True(MountVisibility.Visible(mount,image,true,"admin",true,regions));
        Assert.False(MountVisibility.Visible(mount with {Riders=[]},image,true,"admin",true,[]));
    }
}
