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
            var handler=typeof(ServerMapWebServer).GetMethod("AreaMarkerRequest",BindingFlags.Instance|BindingFlags.NonPublic)!;
            async Task<(int Status,JsonElement Json)> Call(string method,object? body=null,bool login=true,bool header=true)
            {
                using var request=new HttpRequestMessage(new HttpMethod(method),"api/v1/area-markers");if(login)request.Headers.Add("Cookie",cookie);if(header)request.Headers.Add("X-ServerMap-Request","1");if(body!=null)request.Content=new StringContent(JsonSerializer.Serialize(body),Encoding.UTF8,"application/json");
                var task=http.SendAsync(request);var context=await listener.GetContextAsync();
                try { handler.Invoke(web,[context,"api/v1/area-markers"]); } catch { context.Response.StatusCode=500;context.Response.Close();using var failure=await task;throw; }
                using var response=await task;return ((int)response.StatusCode,JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync()));
            }
            object Body(long revision=0,int band=4,int[][]? rects=null)=>new{revision,name="Area",color="#abcdef",minZoom=band,rects=rects??[[0,0,10,10]]};
            Require((await Call("POST",Body(),false)).Status==401,"Guest mutation accepted");
            Require((await Call("POST",Body(),header:false)).Status==403,"CSRF header missing but accepted");
            admin=false;Require((await Call("POST",Body())).Status==403,"Revoked game admin can write");admin=true;
            Require((await Call("PUT",Body())).Status==405,"Unsupported method accepted");
            Require((await Call("POST",Body(band:14))).Status==400,"Invalid zoom band accepted");
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
            Console.WriteLine("PASS area HTTP: guest/player/admin, live role revocation, CSRF, geometry validation, conflicts, neighbour clipping, fog and persistence");
        }
        finally { Directory.Delete(root,true); }
    }
}
