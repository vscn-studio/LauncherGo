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
            File.WriteAllBytes(tile, ServerMap.Render.PngEncoder.Encode(512,512,pixels));
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
    protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.Name switch
    {
        "get_PlayerUID" or "get_PlayerName" => Name,
        "HasPrivilege" => Name=="admin",
        _ => method?.ReturnType.IsValueType == true ? Activator.CreateInstance(method.ReturnType) : null
    };
}
