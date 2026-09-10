using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using ServerMap.Web;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

static class MountedChecks
{
    static void Require(bool ok,string message) { if(!ok)throw new Exception(message); }
    static void Reject(Action action,string code)
    {
        try { action();throw new Exception("Accepted: "+code); }
        catch(MountedTeleportException ex) { Require(ex.Message==code,ex.Message); }
    }
    public static void Run()
    {
        var water=new[]{new MountedTeleportRules.Surface(99,true)};
        var ground=new[]{new MountedTeleportRules.Surface(99,false)};
        double Y(MountedTeleportRules.Kind kind,double altitude=100,double offset=2,bool onGround=true,MountedTeleportRules.Surface[]? source=null,MountedTeleportRules.Surface[]? target=null) =>
            MountedTeleportRules.ArrivalY(kind,altitude,offset,99,0,source??ground,target??ground,onGround);
        Require(Y(MountedTeleportRules.Kind.Airship,target:[new(100,false)])==103,"Airship origin must account for the pilot's 2-block seat offset (pilot arrives at 105)");
        Reject(()=>Y(MountedTeleportRules.Kind.Airship,98,target:[new(100,false)]),"teleport_airship_height");
        Reject(()=>Y(MountedTeleportRules.Kind.Airship,99,target:[new(100,false)]),"teleport_airship_height");
        Require(Y(MountedTeleportRules.Kind.Boat,source:water,target:water)==99,"Boat draft changed");
        Reject(()=>Y(MountedTeleportRules.Kind.Boat,source:ground,target:water),"teleport_water_only");
        Reject(()=>Y(MountedTeleportRules.Kind.Boat,source:water,target:ground),"teleport_water_only");
        Require(Math.Abs(Y(MountedTeleportRules.Kind.Elk)-99.01)<.0001,"Elk landing incorrect");
        Reject(()=>Y(MountedTeleportRules.Kind.Elk,source:water),"teleport_ground_only");
        Reject(()=>Y(MountedTeleportRules.Kind.Elk,target:water),"teleport_ground_only");
        Reject(()=>Y(MountedTeleportRules.Kind.Elk,onGround:false),"teleport_ground_only");
        Reject(()=>Y(MountedTeleportRules.Kind.Elk,target:[ground[0],new(101,false)]),"teleport_ground_only");
        var settings=new PlayerTeleportSettings{EffectsEnabled=true,ItemsPerJump=3,StabilityLossPercent=80,HungerLoss=20,HealthLoss=2};
        Require(settings.Multiply(2).Cost(2)==12 && settings.Multiply(2).HealthLoss==4 && settings.Multiply(2).StabilityLossPercent==160,"Driver multiplier or runtime >100% stability failed");
        Require(settings.Multiply(1)==settings && settings.Multiply(0).Cost(2)==0 && !settings.Multiply(0).EffectsEnabled,"Passenger/admin policy incorrect");
        ItemSlot Slot(int n)=>new(null){Itemstack=new ItemStack(new Item{Code=new(TemporalGearPayment.Code),ItemId=700},n)};
        var a=Slot(5);var b=Slot(0);var calls=0;
        TemporalGearPayment.Charge[] charges=[new([a],2,TemporalGearPayment.Code),new([b],1,TemporalGearPayment.Code)];
        Require(!TemporalGearPayment.ExecuteGroup(charges,()=>calls++) && a.StackSize==5 && calls==0,"Insufficient passenger funds charged driver");
        b.Itemstack=Slot(3).Itemstack;
        try { TemporalGearPayment.ExecuteGroup(charges,()=>throw new IOException());throw new Exception("Failure swallowed"); }
        catch(IOException) { Require(a.StackSize==5 && b.StackSize==3,"Party payment rollback failed"); }
        Require(TemporalGearPayment.ExecuteGroup(charges,()=>calls++) && a.StackSize==3 && b.StackSize==2 && calls==1,"2x/1x payment failed");
        Console.WriteLine("PASS mounted terrain rules, altitude equality, seat-height conversion, 2x/1x effects and atomic party payment");
        HttpChecks().GetAwaiter().GetResult();
    }

    static async Task HttpChecks()
    {
        var root=Directory.CreateTempSubdirectory("LauncherGo-mounted-teleport-").FullName;
        using var listener=new HttpListener();
        var portListener=new TcpListener(IPAddress.Loopback,0);portListener.Start();var port=((IPEndPoint)portListener.LocalEndpoint).Port;portListener.Stop();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");listener.Start();
        using var http=new HttpClient(new HttpClientHandler{UseCookies=false}){BaseAddress=new Uri($"http://127.0.0.1:{port}/")};
        try
        {
            var party=new[]{new TestMountedPlayer("driver",1),new TestMountedPlayer("passenger",2)};
            var mount=new TestMountedBoat{EntityId=10,CollisionBox=new(-.5f,0,-.5f,.5f,1,.5f)};
            mount.Pos.SetPos(100.5,98.5,100.5);mount.Pos.Yaw=.8f;
            Entity controller=party[0];
            var passengers=new Entity?[]{party[0],party[1]};
            IMountableSeat[] seats=[];IMountable? supplier=null;
            supplier=Proxy.Make<IMountable>((m,a)=>m.Name switch {"get_Controller"=>controller,"get_OnEntity"=>mount,"get_Seats"=>seats,_=>Proxy.Default(m.ReturnType)});
            mount.Supplier=supplier;
            seats=Enumerable.Range(0,2).Select(i=>Proxy.Make<IMountableSeat>((m,a)=>m.Name switch {
                "get_SeatId"=>"seat"+i,"get_CanControl"=>true,"get_MountSupplier"=>supplier,"get_Entity"=>mount,"get_Passenger"=>passengers[i],
                "get_SeatPosition"=>new EntityPos(mount.Pos.X+i, mount.Pos.Y+1.5, mount.Pos.Z),_=>Proxy.Default(m.ReturnType)
            })).ToArray();
            foreach(var (player,i) in party.Select((p,i)=>(p,i))) { player.Seat(seats[i]);player.Pos.SetFrom(seats[i].SeatPosition); }
            Require(MountedTeleportRules.IsController(party[0],supplier)&&!MountedTeleportRules.IsController(party[1],supplier),"Controllable passenger was mistaken for actual driver");
            var inventories=party.Select(_=>new[]{new ItemSlot(null){Itemstack=new ItemStack(new Item{Code=new(TemporalGearPayment.Code),ItemId=700},10)}}).ToArray();
            var admin=new[]{false,false};var online=new[]{true,true};
            var players=party.Select((p,i)=>Proxy.Make<IServerPlayer>((m,a)=>m.Name switch {
                "get_Entity"=>p,"get_PlayerUID"=>p.PlayerUID,"get_PlayerName"=>p.PlayerUID,"HasPrivilege"=>admin[i],
                "get_InventoryManager"=>Proxy.Make<IPlayerInventoryManager>((im,ia)=>im.Name=="GetOwnInventory" && (string)ia![0]! =="hotbar"
                    ? Proxy.Make<IInventory>((iv,va)=>iv.Name=="GetEnumerator"?((IEnumerable<ItemSlot>)inventories[i]).GetEnumerator():Proxy.Default(iv.ReturnType)):Proxy.Default(im.ReturnType)),
                _=>Proxy.Default(m.ReturnType)
            })).ToArray();
            var air=new Block{BlockId=0,CollisionBoxes=[]};
            var stone=new Block{BlockId=1,BlockMaterial=EnumBlockMaterial.Stone,CollisionBoxes=[new Cuboidf(0,0,0,1,1,1)]};
            var water=new Block{BlockId=2,BlockMaterial=EnumBlockMaterial.Water,MatterState=EnumMatterState.Liquid,LiquidCode="water",LiquidLevel=7,CollisionBoxes=[]};
            var dry=false;var obstacle=false;var shallow=false;var unloaded=false;
            var blocks=Proxy.Make<IBlockAccessor>((m,a)=>m.Name switch {
                "get_MapSizeX" or "get_MapSizeZ"=>20000,"get_MapSizeY"=>256,"GetRainMapHeightAt"=>98,
                "GetChunkAtBlockPos"=>unloaded?null:Proxy.Make<IWorldChunk>(),
                "GetBlock" when a![0] is BlockPos pos => (a.Length>1?(int)a[1]!:BlockLayersAccess.Solid)==BlockLayersAccess.Fluid
                    ? (!dry && pos.Y is >=95 and <=98 ? water:air)
                    : (pos.Y<95 || dry&&pos.Y==98 || shallow&&pos.X>9000&&pos.Y==98 || obstacle&&pos.X==10001&&pos.Y==100 ? stone:air),
                _=>Proxy.Default(m.ReturnType)
            });
            var notifications=0;var failBroadcast=false;Action? onLoad=null;
            var gameEvents=Proxy.Make<IServerEventAPI>((m,a)=>{
                if(m.Name=="EnqueueMainThreadTask")((Action)a![0]!)();
                return Proxy.Default(m.ReturnType);
            });
            var world=Proxy.Make<IServerWorldAccessor>((m,a)=>m.Name switch {
                "get_AllOnlinePlayers"=>players.Where((_,i)=>online[i]).Cast<IPlayer>().ToArray(),"get_BlockAccessor"=>blocks,
                "PlayerByUid"=>players.Single(p=>p.PlayerUID==(string)a![0]!),_=>Proxy.Default(m.ReturnType)
            });
            var api=Proxy.Make<ICoreServerAPI>((m,a)=>m.Name switch {
                "get_World"=>world,"get_Event"=>gameEvents,"get_ModLoader"=>Proxy.Make<IModLoader>(),"get_Logger"=>Proxy.Make<ILogger>(),
                "get_Network"=>Proxy.Make<IServerNetworkAPI>((nm,na)=>{if(nm.Name=="BroadcastEntityPacket"){notifications++;if(failBroadcast)throw new IOException("Injected network failure");}return Proxy.Default(nm.ReturnType);}),
                "get_WorldManager"=>Proxy.Make<IWorldManagerAPI>((wm,wa)=>{if(wm.Name=="LoadChunkColumnPriority"){onLoad?.Invoke();((ChunkLoadOptions)wa![2]!).OnLoaded();}return Proxy.Default(wm.ReturnType);}),
                _=>Proxy.Default(m.ReturnType)
            });
            mount.Api=api;mount.World=world;
            foreach(var p in party){p.Api=api;p.World=world;}
            var auth=new MapAuthStore(Path.Combine(root,"auth.json"));foreach(var p in players)auth.SetPassword(p,"mounted-test-password");
            var cookies=players.Select(p=>"servermap_auth="+auth.Login(p.PlayerName,"mounted-test-password")!.Value.SessionId).ToArray();
            var settings=new AnnouncementStore(Path.Combine(root,"announcement.json"));
            var policy=new PlayerTeleportSettings{EffectsEnabled=true,HealthLoss=2,HungerLoss=20,StabilityLossPercent=10};
            settings.Save("","https://example.com","test",playerGearTeleportEnabled:true,playerTeleport:policy);
            var notebook=new MapNotebookStore(Path.Combine(root,"notebook.json"));
            var links=new TranslocatorIndex(Path.Combine(root,"links.json"),_=>{});links.ReplaceChunk(3,3,3,[new(100,100,100,10000,100,10000)]);
            var web=(ServerMapWebServer)RuntimeHelpers.GetUninitializedObject(typeof(ServerMapWebServer));
            void Field(string name,object? value)=>typeof(ServerMapWebServer).GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(web,value);
            foreach(var name in new[]{"teleportQuotes","mountedTeleportQuotes","teleportRequests","layerVersions"})
            {var f=typeof(ServerMapWebServer).GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!;f.SetValue(web,Activator.CreateInstance(f.FieldType));}
            Field("api",api);Field("auth",auth);Field("announcements",settings);Field("notebook",notebook);Field("translocators",links);Field("stop",new CancellationTokenSource());Field("waypointRequests",new SemaphoreSlim(8,8));
            var handler=typeof(ServerMapWebServer).GetMethod("HandleTeleport",BindingFlags.Instance|BindingFlags.NonPublic)!;
            async Task<JsonElement> Call(bool preview,object body,int caller=0)
            {
                using var request=new HttpRequestMessage(HttpMethod.Post,preview?"quote":"execute");request.Headers.Add("Cookie",cookies[caller]);request.Headers.Add("X-ServerMap-Request","1");
                request.Content=new StringContent(JsonSerializer.Serialize(body),Encoding.UTF8,"application/json");
                var responseTask=http.SendAsync(request);var context=await listener.GetContextAsync();
                try { handler.Invoke(web,[context,preview]); } catch { context.Response.StatusCode=500;context.Response.Close();using var failed=await responseTask;throw; }
                using var response=await responseTask;return JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
            }
            Task<JsonElement> Quote(int caller=0)=>Call(true,new{x=10000,z=10000},caller);
            async Task Error(string code, bool preview=true, string? id=null,int caller=0)
            {var result=preview?await Quote(caller):await Call(false,new{quoteId=id},caller);Require(result.TryGetProperty("error",out var e)&&e.GetString()==code,$"Expected {code}: {result}");}
            await Error("teleport_mount_disabled");
            settings.Save("","https://example.com","test",mountedTeleportEnabled:true);
            Require(new AnnouncementStore(Path.Combine(root,"announcement.json")).Current.MountedTeleportEnabled,"Mount toggle did not persist");
            await Error("teleport_driver_only",caller:1);
            var q=await Quote();Require(q.TryGetProperty("allowed",out var allowed)&&allowed.GetBoolean(),q.ToString());Require(q.GetProperty("cost").GetInt32()==2,"Driver cost not doubled");
            var members=q.GetProperty("participants").EnumerateArray().ToArray();Require(members.Single(p=>!p.GetProperty("driver").GetBoolean()).GetProperty("cost").GetInt32()==1,"Passenger cost missing");
            inventories[1][0].Itemstack=null;q=await Quote();Require(q.GetProperty("reason").GetString()=="teleport_party_gears" && inventories[0][0].StackSize==10,"Passenger funds were not checked");
            await Error("teleport_party_gears",false,q.GetProperty("quoteId").GetString());inventories[1][0].Itemstack=new ItemStack(new Item{Code=new(TemporalGearPayment.Code),ItemId=700},10);
            party[1].Health.Health=2;q=await Quote();Require(q.GetProperty("reason").GetString()=="teleport_party_health","Passenger lethal effects accepted");party[1].Health.Health=20;
            q=await Quote();controller=party[1];await Error("teleport_driver_only",false,q.GetProperty("quoteId").GetString());controller=party[0];
            q=await Quote();passengers[1]=null;party[1].Seat(null);await Error("teleport_changed",false,q.GetProperty("quoteId").GetString());passengers[1]=party[1];party[1].Seat(seats[1]);
            q=await Quote();online[1]=false;await Error("teleport_offline",false,q.GetProperty("quoteId").GetString());online[1]=true;
            q=await Quote();settings.Save("","https://example.com","test",mountedTeleportEnabled:false);await Error("teleport_mount_disabled",false,q.GetProperty("quoteId").GetString());settings.Save("","https://example.com","test",mountedTeleportEnabled:true);
            q=await Quote();mount.Pos.X+=2;await Error("teleport_changed",false,q.GetProperty("quoteId").GetString());mount.Pos.X-=2;
            dry=true;await Error("teleport_water_only");dry=false;
            obstacle=true;await Error("teleport_mount_space");obstacle=false;
            shallow=true;await Error("teleport_water_only");shallow=false;
            unloaded=true;await Error("teleport_surface");unloaded=false;
            q=await Quote();onLoad=()=>{obstacle=true;};await Error("teleport_mount_space",false,q.GetProperty("quoteId").GetString());onLoad=null;obstacle=false;
            q=await Quote();failBroadcast=true;
            try { await Call(false,new{quoteId=q.GetProperty("quoteId").GetString()});throw new Exception("Injected failure accepted"); }
            catch(TargetInvocationException ex) when(ex.InnerException is IOException) { Require(inventories.All(i=>i[0].StackSize==10)&&mount.Pos.X==100.5&&party[0].Pos.X==100.5,"Failed vehicle transfer did not roll back all entities/payment"); }
            // Close the intentionally failed response before sending the next request.
            failBroadcast=false;
            q=await Quote();var success=await Call(false,new{quoteId=q.GetProperty("quoteId").GetString()});Require(success.GetProperty("ok").GetBoolean(),success.ToString());
            Require(inventories[0][0].StackSize==8&&inventories[1][0].StackSize==9,"Per-player payment incorrect");
            Require(party[0].Health.Health==16&&party[1].Health.Health==18,"Per-player effects incorrect");
            Require(mount.Pos.X==10000.5&&party[0].Pos.X==10000.5&&party[1].Pos.X==10001.5&&party.All(p=>p.MountedOn!=null)&&Math.Abs(mount.Pos.Yaw-.8f)<1e-6&&notifications>=2,"Native player teleport did not preserve the whole party, seat offsets, direction or broadcasts");
            await Error("teleport_expired",false,q.GetProperty("quoteId").GetString());
            void ResetParty()
            {
                mount.Pos.SetPos(100.5,98.5,100.5);
                for(var i=0;i<party.Length;i++){party[i].Pos.SetFrom(seats[i].SeatPosition);party[i].Health.Health=20;inventories[i][0].Itemstack=new ItemStack(new Item{Code=new(TemporalGearPayment.Code),ItemId=700},10);}
            }
            ResetParty();
            q=await Quote();admin[1]=true;await Error("teleport_changed",false,q.GetProperty("quoteId").GetString());admin[1]=false;
            q=await Quote();settings.Save("","https://example.com","test",playerTeleport:policy with {HealthLoss=3});await Error("teleport_changed",false,q.GetProperty("quoteId").GetString());settings.Save("","https://example.com","test",playerTeleport:policy);
            admin[0]=true;
            settings.Save("","https://example.com","test",playerGearTeleportEnabled:false);
            await Error("teleport_disabled");
            settings.Save("","https://example.com","test",playerGearTeleportEnabled:true);
            var hidden=notebook.SaveRegion(null,"Passenger overhang",10001,10000,10002,10001);
            await Error("Hidden region");notebook.RemoveRegion(hidden.Id);
            q=await Quote();Require(q.GetProperty("cost").GetInt32()==0 && q.GetProperty("participants")[1].GetProperty("cost").GetInt32()==1,"Admin driver exempted ordinary passenger");
            success=await Call(false,new{quoteId=q.GetProperty("quoteId").GetString()});
            Require(success.GetProperty("ok").GetBoolean()&&inventories[0][0].StackSize==10&&inventories[1][0].StackSize==9&&party[0].Health.Health==20&&party[1].Health.Health==18,"Admin/ordinary mixed-party fees and effects incorrect");
            ResetParty();admin[1]=true;settings.Save("","https://example.com","test",playerGearTeleportEnabled:false);
            q=await Quote();Require(q.GetProperty("allowed").GetBoolean()&&q.GetProperty("jumps").GetInt32()==0,"All-admin party exemption failed");
            var reversible=TeleportEffects.PrepareReversible(party[0],policy);
            try
            {
                TemporalGearPayment.ExecuteGroup([new(inventories[0],2,TemporalGearPayment.Code)],()=>MountedTeleportTransfer.Move(api,mount,10000.5,100,10000.5,()=>{reversible.Apply();throw new IOException("Effect-phase failure");}));
                throw new Exception("Effect-phase failure accepted");
            }
            catch(IOException){reversible.Restore();Require(inventories[0][0].StackSize==10&&mount.Pos.X==100.5&&party[0].Health.Health==20,"Effect-phase failure did not restore fees, effects and position");}
            Console.WriteLine("PASS admin/ordinary mixed parties, revoked admin, policy changes, hidden passenger overhang, all-admin exemption and post-move effect rollback");
            Console.WriteLine("PASS mounted HTTP pipeline: disabled/default persistence, actual controller, 2x/1x quote/payment/effects, insufficient/lethal passenger, driver/seat/offline/setting/motion changes, unsafe/unloaded/late-obstructed destination, rollback, native seated multi-player sync and replay rejection");
        }
        finally { listener.Stop();Directory.Delete(root,true); }
    }
}

public class Proxy : DispatchProxy
{
    System.Func<MethodInfo,object?[]?,object?>? handler;
    public static T Make<T>(System.Func<MethodInfo,object?[]?,object?>? handler=null) where T:class
    {var value=Create<T,Proxy>();((Proxy)(object)value).handler=handler;return value;}
    public static object? Default(Type type)=>type==typeof(void)?null:type.IsValueType?Activator.CreateInstance(type):null;
    protected override object? Invoke(MethodInfo? method,object?[]? args)=>handler==null?Default(method!.ReturnType):handler(method!,args);
}
sealed class TestMountedBoat : EntityBoat
{
    public IMountable Supplier=null!;
    public override T GetInterface<T>()=>Supplier as T ?? null!;
    public override T GetBehavior<T>()=>null!;
}
sealed class TestMountedPlayer : EntityPlayer
{
    readonly List<EntityBehavior> behaviors=[];
    public EntityBehaviorHealth Health;
    public TestMountedPlayer(string uid,long id)
    {
        EntityId=id;WatchedAttributes.SetString("playerUID",uid);CollisionBox=new(-.3f,0,-.3f,.3f,1.8f,.3f);
        WatchedAttributes.SetAttribute("health",new TreeAttribute());var hungerTree=new TreeAttribute();WatchedAttributes.SetAttribute("hunger",hungerTree);
        Health=new(this);Health.Health=20;
        var hunger=new EntityBehaviorHunger(this);typeof(EntityBehaviorHunger).GetField("hungerTree",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(hunger,hungerTree);hunger.Saturation=500;
        var stability=new EntityBehaviorTemporalStabilityAffected(this);stability.OwnStability=1;
        behaviors.AddRange([Health,hunger,stability]);
    }
    public void Seat(IMountableSeat? seat)=>MountedOn=seat;
    public override T GetBehavior<T>()=>behaviors.OfType<T>().FirstOrDefault()!;
}
