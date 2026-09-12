using System.Reflection;
using ServerMap;
using ServerMap.Web;
using Vintagestory.API.Common;
using Vintagestory.API.Server;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

// Only installed into a fresh temporary save by test-map-notebook-api.ps1.
public sealed class NotebookFixture : ModSystem
{
    public override double ExecuteOrder() => 1;
    public override void StartServerSide(ICoreServerAPI api)
    {
        api.Event.ServerRunPhase(EnumServerRunPhase.RunGame, () =>
        {
            var mod = api.ModLoader.GetModSystem<ServerMapModSystem>();
            object Field(string name) => typeof(ServerMapModSystem).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(mod)!;
            var auth = (MapAuthStore)Field("authStore");
            var web = (ServerMapWebServer)Field("web");
            // Isolated HTTP fixture: freeze the real mount sync and submit bounded synthetic
            // rider-owned PNGs through the same store/decoder used by network uploads.
            api.ModLoader.GetModSystem<MountMapSyncSystem>().Dispose();
            var mountStore = web.Mounts;
            var mountNow = Environment.TickCount64;
            mountStore.Replace([
                new(9001,"fixture:boat","Shared boat",2000,100,2000,(float)(Math.PI/2),[new("alice","Alice",2000,2000),new("bob","Bob",2000,2000)]),
                new(9002,"fixture:elk","Bob elk",2100,100,2000,0,[new("bob","Bob",2100,2000)]),
                new(9003,"fixture:bad","Invalid image",2200,100,2000,0,[new("alice","Alice",2200,2000)])
            ],mountNow);
            var mountPixels = new byte[1024*1024*4];
            for(var i=0;i<mountPixels.Length;i+=4){mountPixels[i]=120;mountPixels[i+1]=200;mountPixels[i+3]=255;}
            var mountPng = ServerMap.Render.PngEncoder.Encode(1024,1024,mountPixels);
            var trackStore = (PlayerTrackStore)typeof(ServerMapWebServer).GetField("trackStore", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(web)!;
            var trackImageKey = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(mountPng));
            trackStore.SaveMountImage(trackImageKey, mountPng);
            var historicTrack = trackStore.Start("track-fixture", "Track fixture", "admin", 120);
            for (var i = 0; i < 10; i++) trackStore.Append(historicTrack.Id, new(historicTrack.Started.AddMilliseconds(i), 2000 + i, 100, 2000, 0, new("Shared boat",2000+i,100,2000,0) { ImageKey=trackImageKey,WorldSize=8,CenterZ=-1,DisplayScale=2 }));
            trackStore.Stop(historicTrack.Id, "fixture");
            foreach(var (id,uid) in new[]{(9001L,"alice"),(9002L,"bob"),(9003L,"alice")})
            {
                var mountToken=mountStore.Request(uid,id,mountNow)!;
                mountStore.Receive(uid,id,mountToken,0,1,id==9003?[1,2,3]:mountPng,8,0,-1,mountNow,id==9001?4:12);
            }
            var mountControl=Path.Combine((string)Field("dataRoot"),"dismount.test");
            api.Event.RegisterGameTickListener(_=>{if(File.Exists(mountControl))mountStore.Replace([],Environment.TickCount64);},250);
            using (var bitmap = new SkiaSharp.SKBitmap(1600, 800))
            {
                bitmap.Erase(SkiaSharp.SKColors.CornflowerBlue);
                using var image = SkiaSharp.SKImage.FromBitmap(bitmap);
                foreach (var format in new[] { SkiaSharp.SKEncodedImageFormat.Jpeg, SkiaSharp.SKEncodedImageFormat.Png, SkiaSharp.SKEncodedImageFormat.Webp })
                {
                    using var encoded = image.Encode(format, 85);
                    File.WriteAllBytes(Path.Combine((string)Field("dataRoot"), "poi-test." + format.ToString().ToLowerInvariant()), encoded.ToArray());
                }
            }
            // Synthetic PNGs exercise the real decoder, cache and HTTP endpoint.
            var avatarRoot = Path.Combine((string)Field("dataRoot"), "avatar-test-layers");
            var avatarPixels = new byte[256*256*4];
            for(var i=0;i<avatarPixels.Length;i+=4){avatarPixels[i]=42;avatarPixels[i+1]=90;avatarPixels[i+3]=255;}
            foreach(var file in new[]{"baseskin/skin2.png","eyecolor/amethyst.png","hairbase/bald/lightgray.png"})
            {
                var path=Path.Combine(avatarRoot,file);Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path,ServerMap.Render.PngEncoder.Encode(256,256,avatarPixels));
            }
            var mapConfig=Field("config");mapConfig.GetType().GetProperty("AvatarAssetsPath")!.SetValue(mapConfig,avatarRoot);
            typeof(ServerMapWebServer).GetMethod("InitializeAvatars",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(web,null);
            var clientAvatars = web.ClientAvatars!;
            var token = clientAvatars.Request("avatar-fixture", "head-fixture", Environment.TickCount64)!;
            var scene = new ServerMap.Render.AvatarScene {
                Textures = [new(1,1,[128,180,220,255])],
                Vertices = [new(0,-1,-1,0,0,0),new(0,-1,1,1,0,0),new(0,1,0,.5f,1,0)]
            };
            clientAvatars.Receive("avatar-fixture", token, 0, 1, scene.Pack(), Environment.TickCount64);
            var bobToken = clientAvatars.Request("bob", "head-fixture", Environment.TickCount64)!;
            clientAvatars.Receive("bob", bobToken, 0, 1, scene.Pack(), Environment.TickCount64);
            var explorationControl = Path.Combine((string)Field("dataRoot"), "explore-bob.test");
            long explorationTick = 0;
            explorationTick = api.Event.RegisterGameTickListener(_ => {
                if (!File.Exists(explorationControl)) return;
                var player = DispatchProxy.Create<IServerPlayer, TestPlayer>();
                var proxy = (TestPlayer)(object)player; proxy.Name = "bob"; proxy.Entity = new EntityPlayer(); proxy.Entity.Pos.SetPos(160, 100, 160);
                // Explicit synthetic native map pieces for this fixture, not position-based exploration.
                web.RecordVerifiedExploration(player, from x in Enumerable.Range(1, 9) from z in Enumerable.Range(1, 9) select ServerMap.Network.MapExplorationProtocol.Cell(x, z));
                api.Event.UnregisterGameTickListener(explorationTick);
            }, 250);
            var nativeControl = Path.Combine((string)Field("dataRoot"), "explore-bob-native.test");
            string? lastNativeControl = null;
            api.Event.RegisterGameTickListener(_ => {
                if (!File.Exists(nativeControl)) return;
                var value = File.ReadAllText(nativeControl); if (value == lastNativeControl) return;
                var coordinates = value.Split(',');
                if (coordinates.Length != 2 || !int.TryParse(coordinates[0], out var x) || !int.TryParse(coordinates[1], out var z)) return;
                var player = DispatchProxy.Create<IServerPlayer, TestPlayer>(); ((TestPlayer)(object)player).Name = "bob";
                web.RecordVerifiedExploration(player, [ServerMap.Network.MapExplorationProtocol.Cell(x, z)]);
                lastNativeControl = value;
            }, 250);
            long clientAvatarTick = 0;
            clientAvatarTick = api.Event.RegisterGameTickListener(_ => {
                var key = clientAvatars.GetKey("avatar-fixture", "head-fixture"); if (key == null) return;
                File.WriteAllText(Path.Combine((string)Field("dataRoot"),"client-avatar-key.test"),key);api.Event.UnregisterGameTickListener(clientAvatarTick);
            },250);
            var avatars=(LocalAvatarCache)typeof(ServerMapWebServer).GetField("avatars",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(web)!;
            var look=new LocalAvatarRenderer.Appearance("skin2","amethyst","bald","none","none","none","lightgray");
            long avatarTick=0;
            avatarTick=api.Event.RegisterGameTickListener(_=>{
                var key=avatars.Request(look);if(key==null)return;
                File.WriteAllText(Path.Combine((string)Field("dataRoot"),"avatar-key.test"),key);
                api.Event.UnregisterGameTickListener(avatarTick);
            },250);
            foreach (var name in new[] {"alice", "bob", "admin"})
            {
                var server = (Vintagestory.Server.ServerMain)api.World;
                var data = server.PlayerDataManager.GetOrCreateServerPlayerData(name);
                data.RoleCode = name == "admin" ? server.Config.RolesByCode.Values.First(role => role.Privileges.Contains("root")).Code : server.Config.DefaultRoleCode;
                var player = DispatchProxy.Create<IServerPlayer, TestPlayer>();
                ((TestPlayer)(object)player).Name = name;
                auth.SetPassword(player, "notebook-test-password");
                var svgPath = Environment.GetEnvironmentVariable("MAP_TEST_ICON_FILE");
                var svg = string.IsNullOrEmpty(svgPath) ? System.Text.Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 20 20\"><path d=\"M0 0 L20 20\"/></svg>") : File.ReadAllBytes(svgPath);
                web.ReceiveWaypointIcon(player, new ServerMap.Network.ClientWaypointIconPacket { Name = name == "admin" ? "pick" : "forbidden", Data = svg });
            }
            var layer = api.ModLoader.GetModSystem<WorldMapManager>().MapLayers.OfType<WaypointMapLayer>().Single();
            layer.Waypoints.Add(new Waypoint { Guid="alice-marker", OwningPlayerUid="alice", Title="Alice mine", Text="private", Icon="pick", Color=0x123456, Position=new Vec3d(64.25,110,72.75), Pinned=true });
            layer.Waypoints.Add(new Waypoint { Guid="bob-marker", OwningPlayerUid="bob", Title="Bob mine", Icon="home", Color=0x654321, Position=new Vec3d(100,110,100) });
            var tile = Path.Combine((string)Field("dataRoot"), "2d", "basic", "0", "0_0.png");
            Directory.CreateDirectory(Path.GetDirectoryName(tile)!);
            var pixels = new byte[512*512*4];
            for(var i=0;i<pixels.Length;i+=4){pixels[i]=30;pixels[i+1]=60;pixels[i+2]=90;pixels[i+3]=255;}
            var fixturePng = ServerMap.Render.PngEncoder.Encode(512,512,pixels);
            File.WriteAllBytes(tile, fixturePng);
            // Mobile area editing zooms out one level; provide the matching synthetic parent terrain too.
            var parentTile = Path.Combine((string)Field("dataRoot"), "2d", "basic", "1", "0_0.png");
            Directory.CreateDirectory(Path.GetDirectoryName(parentTile)!); File.WriteAllBytes(parentTile, fixturePng);
            api.Logger.Notification("Map notebook test fixture ready");
            // Test-only trigger: revoke root while keeping the existing HTTP session.
            var control = Path.Combine((string)Field("dataRoot"), "revoke-admin.test");
            api.Event.RegisterGameTickListener(_ => {
                if (!File.Exists(control)) return;
                var server = (Vintagestory.Server.ServerMain)api.World;
                server.PlayerDataManager.GetOrCreateServerPlayerData("admin").RoleCode = server.Config.DefaultRoleCode;
            }, 500);
        });
    }
}
public class TestPlayer : DispatchProxy
{
    public string Name = "";
    public EntityPlayer? Entity;
    protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.Name switch
    {
        "get_PlayerUID" or "get_PlayerName" => Name,
        "get_Entity" => Entity,
        "HasPrivilege" => Name=="admin",
        _ => method?.ReturnType.IsValueType == true ? Activator.CreateInstance(method.ReturnType) : null
    };
}
