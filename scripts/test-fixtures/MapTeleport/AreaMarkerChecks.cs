using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using ServerMap.Web;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

static class AreaMarkerChecks
{
    public static void Run() => HttpChecks().GetAwaiter().GetResult();
    static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    static async Task HttpChecks()
    {
        var root=Directory.CreateTempSubdirectory("LauncherGo-area-http-").FullName;
        using var listener=new HttpListener();var reserve=new TcpListener(IPAddress.Loopback,0);reserve.Start();var port=((IPEndPoint)reserve.LocalEndpoint).Port;reserve.Stop();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");listener.Start();
        using var http=new HttpClient(new HttpClientHandler { UseCookies=false }) { BaseAddress=new Uri($"http://127.0.0.1:{port}/"),Timeout=TimeSpan.FromSeconds(10) };
        try
        {
            var admin=true;
            var player=Proxy.Make<IServerPlayer>((m,a)=>m.Name switch {"get_PlayerUID"=>"admin","get_PlayerName"=>"admin","HasPrivilege"=>admin,_=>Proxy.Default(m.ReturnType)});
            var world=Proxy.Make<IServerWorldAccessor>((m,a)=>m.Name=="get_AllOnlinePlayers"?new IPlayer[]{player}:Proxy.Default(m.ReturnType));
            var api=Proxy.Make<ICoreServerAPI>((m,a)=>m.Name=="get_World"?world:Proxy.Default(m.ReturnType));
            var auth=new MapAuthStore(Path.Combine(root,"auth.json"));auth.SetPassword(player,"test-password");var cookie="servermap_auth="+auth.Login("admin","test-password")!.Value.SessionId;
            var notebook=new MapNotebookStore(Path.Combine(root,"notebook.json"));var areas=new AreaMarkerStore(Path.Combine(root,"areas.json"));
            var web=(ServerMapWebServer)RuntimeHelpers.GetUninitializedObject(typeof(ServerMapWebServer));
            void Field(string name,object value)=>typeof(ServerMapWebServer).GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(web,value);
            Field("api",api);Field("auth",auth);Field("notebook",notebook);Field("areaMarkers",areas);Field("events",new LiveEventHub());
            var settings=new AnnouncementStore(Path.Combine(root,"announcement.json"));settings.Save("","","test",management:new(){FogEnabled=false});
            Field("announcements",settings);Field("config",new ServerMap.Configuration.ServerMapConfig());
            Field("poiImages",new PoiImageStore(Path.Combine(root,"poi-images")));
            var versions=new System.Collections.Concurrent.ConcurrentDictionary<string,long>();foreach(var key in new[]{"players","mounts","spawn","claims","claim-areas","chunks","translocators","pois"})versions[key]=1;Field("layerVersions",versions);
            var manifest=JsonSerializer.SerializeToElement(typeof(ServerMapWebServer).GetMethod("Manifest",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(web,null));
            Require(manifest.GetProperty("layers").EnumerateArray().Where(l=>l.GetProperty("visible").GetBoolean()).Select(l=>l.GetProperty("id").GetString()).Order().SequenceEqual(new[]{"mounts","players","pois","spawn"}),"Unexpected default map layers");
            var handler=typeof(ServerMapWebServer).GetMethod("AreaMarkerRequest",BindingFlags.Instance|BindingFlags.NonPublic)!;
            async Task<(int Status,JsonElement Json)> Call(string method,object? body=null,bool login=true,bool header=true,bool poi=false)
            {
                using var request=new HttpRequestMessage(new HttpMethod(method),poi?"api/v1/pois":"api/v1/area-markers");if(login)request.Headers.Add("Cookie",cookie);if(header)request.Headers.Add("X-ServerMap-Request","1");if(body!=null)request.Content=new StringContent(JsonSerializer.Serialize(body),Encoding.UTF8,"application/json");
                var task=http.SendAsync(request);var context=await listener.GetContextAsync();
                try { if(poi)typeof(ServerMapWebServer).GetMethod("HandlePoiWrite",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(web,[context]);else handler.Invoke(web,[context,"api/v1/area-markers"]); } catch { context.Response.StatusCode=500;context.Response.Close();using var failure=await task;throw; }
                using var response=await task;return ((int)response.StatusCode,JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync()));
            }
            object Body(long revision=0,int band=4,int[][]? rects=null)=>new{revision,name="Area",color="#abcdef",minZoom=band,rects=rects??[[0,0,10,10]]};
            Require((await Call("POST",Body(),false)).Status==401,"Guest mutation accepted");
            Require((await Call("POST",Body(),header:false)).Status==403,"CSRF header missing but accepted");
            admin=false;Require((await Call("POST",Body())).Status==403,"Revoked game admin can write");admin=true;
            Require((await Call("PUT",Body())).Status==405,"Unsupported method accepted");
            Require((await Call("POST",Body(band:16))).Status==400,"Invalid zoom range accepted");
            Require((await Call("POST",Body(rects:[[0,0,0,10]]))).Status==400,"Empty rect accepted");
            Require((await Call("POST",new{revision=0,name="Bad",color="#abcdef",minZoom=4,rects=new[]{new[]{0d,0d,1.5,10d}}})).Status==400,"Subpixel world coordinate accepted");
            Require((await Call("POST",Body())).Status==200,"Admin save failed");
            var publicData=await Call("GET",login:false);Require(publicData.Status==200&&publicData.Json.GetProperty("markers").GetArrayLength()==1,"Public areas missing");
            Require((await Call("POST",Body())).Status==409,"Stale revision accepted");
            Require((await Call("POST",Body(1,rects:[[5,0,20,10]]))).Status==200,"Adjacent area failed");
            var saved=areas.Read();Require(saved.Markers[1].Rects.All(r=>r.MinX>=10),"Server did not trim overlap");
            notebook.SaveRegion(null,"Private",0,0,9,9);
            publicData=await Call("GET",login:false);Require(publicData.Json.GetProperty("markers").GetArrayLength()==1,"Hidden terrain marker disclosed");
            Require((await Call("GET")).Json.GetProperty("markers").GetArrayLength()==2,"Admin cannot inspect hidden area marker");
            admin=false;Require((await Call("DELETE",new{revision=2,id=saved.Markers[0].Id})).Status==403,"Player delete accepted");admin=true;
            Require((await Call("DELETE",new{revision=2,id=saved.Markers[0].Id})).Status==200,"Admin delete failed");Require(notebook.Regions.Length==1,"Area deletion changed fog");
            Require(new AreaMarkerStore(Path.Combine(root,"areas.json")).Read().Markers.Length==1,"Persistence failed");
            var sourceA=areas.Save(3,null,"Fine","#abcdef",12,[new(100,100,110,110)]);
            var sourceB=areas.Save(4,null,"Middle","#abcdef",10,[new(105,105,115,115)]);
            object Merge(int band=7,double border=.45)=>new{revision=5,minZoom=band,name="Merged",color="#123456",sourceIds=new[]{sourceA.Id,sourceB.Id},rects=new[]{new[]{900,900,1000,1000}},borderOpacity=border,fillOpacity=.1,textOpacity=.8};
            Require((await Call("POST",Merge(10))).Status==400,"Same-level merge accepted");
            Require((await Call("POST",Merge(border:1.1))).Status==400,"Invalid opacity accepted");
            admin=false;Require((await Call("POST",Merge())).Status==403,"Player merge accepted");admin=true;
            Require((await Call("POST",Merge())).Status==200,"Higher-level merge failed");
            var merged=areas.Read().Markers.Single(m=>m.Name=="Merged");Require(merged.Rects.All(r=>r.MaxX<=115)&&merged.Style.BorderOpacity==.45,"Merge trusted client geometry or lost opacity");
            Require(areas.Read().Markers.Any(m=>m.Id==sourceA.Id)&&areas.Read().Markers.Any(m=>m.Id==sourceB.Id),"Merge removed sources");
            var visibleStyle=(await Call("GET")).Json.GetProperty("markers").EnumerateArray().Single(m=>m.GetProperty("id").GetString()==merged.Id);
            Require(visibleStyle.GetProperty("textOpacity").GetDouble()==.8,"HTTP style fields missing");
            object RangeBody(int min,int max,int x1,int x2)=>new{revision=areas.Read().Revision,name="Custom",color="#abcdef",minZoom=min,maxZoom=max,rects=new[]{new[]{x1,500,x2,510}}};
            Require((await Call("POST",RangeBody(7,10,500,510))).Status==200,"Custom range rejected");
            Require((await Call("POST",RangeBody(10,13,505,520))).Status==200,"Shared endpoint range rejected");
            Require(areas.Read().Markers.Last().Rects.All(r=>r.MinX>=510),"Shared display level overlap was not clipped");
            Require((await Call("POST",RangeBody(11,15,500,510))).Status==200,"Disjoint display ranges incorrectly clipped");
            foreach(var (min,max) in new[]{(3,15),(4,16),(10,9)}) Require((await Call("POST",RangeBody(min,max,600,610))).Status==400,"Invalid range accepted");
            Require((await Call("POST",new{revision=areas.Read().Revision,name="Bad",color="#abcdef",minZoom=4,maxZoom=5.5,rects=new[]{new[]{600,500,610,510}}})).Status==400,"Fractional zoom accepted");
            var pointStore=new PoiStore(Path.Combine(root,"pois.json"));Field("pois",pointStore);Field("config",new ServerMap.Configuration.ServerMapConfig());
            object PoiBody(string? id=null,int min=4,int max=8)=>new{id,name="Place",text="Description",x=800,z=800,minZoom=min,maxZoom=max};
            Require((await Call("POST",PoiBody(),poi:true)).Status==200,"Admin POI range failed");
            var point=pointStore.All.Single();Require(point.MinZoom==4&&point.MaxZoom==8,"POI range not persisted");
            admin=false;Require((await Call("POST",PoiBody(point.Id),poi:true)).Status==403,"Ordinary owner changed display range");
            Require((await Call("POST",new{id=point.Id,name="Renamed",x=800,z=800},poi:true)).Status==200,"Ordinary edit without zoom fields rejected");
            Require(pointStore.All.Single().MaxZoom==8,"Ordinary edit reset administrator range");admin=true;
            foreach(var (min,max) in new[]{(3,15),(4,16),(10,9)}) Require((await Call("POST",PoiBody(point.Id,min,max),poi:true)).Status==400,"Invalid POI range accepted");
            Require((await Call("POST",new{id=point.Id,minZoom=5,maxZoom=5.5,x=800,z=800},poi:true)).Status==400,"Fractional POI range accepted");
            Require((await Call("POST",new{id=point.Id,minZoom=5,x=800,z=800},poi:true)).Status==400,"Partial POI range accepted");
            Require((await Call("POST",PoiBody(point.Id,5,5),poi:true)).Status==200,"Single-level POI range rejected");
            Console.WriteLine("PASS display ranges: legacy/custom persistence, integer bounds, shared-level clipping, live-admin POI writes and ordinary-edit preservation");
            Console.WriteLine("PASS area HTTP: guest/player/admin, live role revocation, CSRF, geometry validation, conflicts, neighbour clipping, fog and persistence");
        }
        finally { Directory.Delete(root,true); }
    }
}
