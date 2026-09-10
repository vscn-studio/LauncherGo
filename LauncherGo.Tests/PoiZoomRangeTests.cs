using ServerMap.Web;
using Xunit;

namespace LauncherGo.Tests;

public class PoiZoomRangeTests
{
    [Fact] public void OnlyAdminCanChangeRangeAndOrdinaryEditsPreserveIt()
    {
        var root=Directory.CreateTempSubdirectory("poi-zoom-tests-").FullName;
        try
        {
            var path=Path.Combine(root,"pois.json");var store=new PoiStore(path);
            var input=new PoiStore.Poi("","text","Name","Text","#abcdef",0,0,0,null,null,"alice",DateTimeOffset.UtcNow);
            Assert.Equal(PoiStore.SaveResult.Saved,store.TrySave(input,"alice",10,false,out var saved));Assert.Equal(13,saved!.MinZoom);Assert.Equal(15,saved.MaxZoom);
            var edit=saved with{MinZoom=4,MaxZoom=8};
            Assert.Equal(PoiStore.SaveResult.Forbidden,store.TrySave(edit,"alice",10,false,out _,replaceZoom:true));
            Assert.Equal(PoiStore.SaveResult.Saved,store.TrySave(edit,"admin",10,true,out saved,replaceZoom:true));Assert.Equal("alice",saved!.OwnerUid);
            Assert.Equal(PoiStore.SaveResult.Saved,store.TrySave(saved with{Name="Renamed",MinZoom=13,MaxZoom=15},"alice",10,false,out saved));
            Assert.Equal(4,saved!.MinZoom);Assert.Equal(8,new PoiStore(path).All.Single().MaxZoom);
            foreach(var (min,max) in new[]{(3,15),(4,16),(10,9)}) Assert.Throws<ArgumentException>(()=>store.TrySave(saved with{MinZoom=min,MaxZoom=max},"admin",10,true,out _,replaceZoom:true));
            Assert.Equal(PoiStore.SaveResult.Saved,store.TrySave(saved with{MinZoom=4,MaxZoom=4},"admin",10,true,out saved,replaceZoom:true));Assert.Equal(4,saved!.MaxZoom);
        }
        finally { Directory.Delete(root,true); }
    }
}
