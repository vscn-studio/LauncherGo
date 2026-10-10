using System.Collections;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;
using VsslAuth.Server;

if (args.Length != 1) throw new ArgumentException("GameRoot required");
var gameRoot = Path.GetFullPath(args[0]);
AppDomain.CurrentDomain.AssemblyResolve += (_, request) =>
{
    var name = new AssemblyName(request.Name).Name + ".dll";
    foreach (var folder in new[] { gameRoot, Path.Combine(gameRoot, "Lib"), Path.Combine(gameRoot, "Mods") })
    {
        var file = Path.Combine(folder, name);
        if (File.Exists(file)) return Assembly.LoadFrom(file);
    }
    return null;
};
Checks.Run();

static class Checks
{
    static readonly Type SystemType = typeof(VsslAuthServerSystem);
    static readonly BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;
    static object Get(object target, string name) => target.GetType().GetField(name, PrivateInstance)!.GetValue(target)!;
    static void Set(object target, string name, object value) => target.GetType().GetField(name, PrivateInstance)!.SetValue(target, value);
    static object? Call(object target, string name, params object[] args) => target.GetType().GetMethod(name, PrivateInstance)!.Invoke(target, args);
    static object Settings(VsslAuthServerSystem system) => Get(system, "_settings");
    static T Property<T>(object target, string name) => (T)target.GetType().GetProperty(name)!.GetValue(target)!;
    static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    static void Reload(VsslAuthServerSystem system, string json)
    {
        File.WriteAllText(Path.Combine(GamePaths.ModConfig, "serverauth.json"), json);
        Call(system, "ReloadSettings");
    }
    static void Reject(VsslAuthServerSystem system, string json)
    {
        var old = Settings(system);
        try { Reload(system, json); throw new Exception("Invalid configuration accepted"); }
        catch (TargetInvocationException) { Require(ReferenceEquals(old, Settings(system)), "Failed reload replaced active configuration"); }
    }
    static string External(string prefix) => JsonSerializer.Serialize(new
    {
        Enabled = true,
        Discourse = new { Enabled = true, BaseUrl = "https://example.com/", SharedSecret = "test", ListenPrefix = prefix }
    });
    static string Prefix()
    {
        using var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        return "http://127.0.0.1:" + ((IPEndPoint)socket.LocalEndpoint).Port + "/";
    }

    public static void Run()
    {
        var directory = Directory.CreateTempSubdirectory("launchergo-auth-reload-");
        var originalPath = GamePaths.DataPath;
        GamePaths.DataPath = directory.FullName;
        Directory.CreateDirectory(GamePaths.ModConfig);
        var system = new VsslAuthServerSystem();
        try
        {
            Reload(system, """{"Enabled":true,"LoginTimeoutSeconds":120,"RememberSessionMinutes":45}""");
            Require(Property<bool>(Settings(system), "Enabled"), "Enablement not applied");
            Require(Property<int>(Settings(system), "LoginTimeoutSeconds") == 120, "Timeout not applied");
            Reject(system, "{");
            Reject(system, "null");
            File.Delete(Path.Combine(GamePaths.ModConfig, "serverauth.json"));
            var old = Settings(system);
            try { Call(system, "ReloadSettings"); throw new Exception("Missing config accepted"); }
            catch (TargetInvocationException) { Require(ReferenceEquals(old, Settings(system)), "Missing config replaced policy"); }

            Reload(system, External(Prefix()));
            var listener = (HttpListener)Get(system, "_listener");
            Require(listener.IsListening, "External callback listener not started");
            Reject(system, External("invalid-prefix"));
            Require(ReferenceEquals(listener, Get(system, "_listener")) && listener.IsListening, "Invalid prefix stopped old listener");
            using (var occupied = new HttpListener())
            {
                var prefix = Prefix();
                occupied.Prefixes.Add(prefix);
                occupied.Start();
                Reject(system, External(prefix));
                Require(listener.IsListening, "Binding failure stopped old listener");
            }
            Reload(system, External(Prefix()));
            Require(!ReferenceEquals(listener, Get(system, "_listener")), "Listener did not change ports");
            using (var client = new HttpClient())
            {
                var active = (HttpListener)Get(system, "_listener");
                var response = client.GetAsync(active.Prefixes.Single() + "unknown").GetAwaiter().GetResult();
                Require(response.StatusCode == HttpStatusCode.NotFound, "Replacement listener did not serve requests");
            }

            var speed = 1.75f;
            var messages = new List<string>();
            var worldData = Proxy.Make<IWorldPlayerData>((method, args) => method.Name switch
            {
                "get_MoveSpeedMultiplier" => speed,
                "set_MoveSpeedMultiplier" => SetSpeed((float)args![0]!),
                _ => Proxy.Default(method)
            });
            object? SetSpeed(float value) { speed = value; return null; }
            var player = Proxy.Make<IServerPlayer>((method, args) => method.Name switch
            {
                "get_PlayerUID" => "waiting",
                "get_PlayerName" => "Waiting",
                "get_IpAddress" => "127.0.0.1",
                "get_ConnectionState" => EnumClientState.Playing,
                "get_WorldData" => worldData,
                "SendMessage" => RecordMessage((string)args![1]!),
                _ => Proxy.Default(method)
            });
            object? RecordMessage(string message) { messages.Add(message); return null; }
            var world = Proxy.Make<IServerWorldAccessor>((method, _) => method.Name switch
            {
                "PlayerByUid" => player,
                "get_AllOnlinePlayers" => new IPlayer[] { player },
                _ => Proxy.Default(method)
            });
            var logger = Proxy.Make<ILogger>((method, _) => Proxy.Default(method));
            var api = Proxy.Make<ICoreServerAPI>((method, _) => method.Name switch
            {
                "get_World" => world,
                "get_Logger" => logger,
                _ => Proxy.Default(method)
            });
            Set(system, "_api", api);
            Reload(system, """{"Enabled":true}""");
            Call(system, "BeginPending", player, DateTimeOffset.UtcNow.AddSeconds(-30));
            Require(speed == 0, "Pending player was not restricted");
            var pending = (IDictionary)Get(system, "_pendingByUid");
            var pendingState = pending["waiting"]!;
            Reload(system, """{"Enabled":true,"LoginTimeoutSeconds":180}""");
            Require(ReferenceEquals(pendingState, pending["waiting"]), "Reload discarded original movement state");
            Require(Property<DateTimeOffset>(pendingState, "DeadlineUtc") > DateTimeOffset.UtcNow.AddSeconds(170), "Pending deadline not renewed");
            Require(messages.Count > 0 && speed == 0, "Pending login not reissued");

            var challengeType = SystemType.GetNestedType("OAuth2ChallengeState", BindingFlags.NonPublic)!;
            var challenge = Activator.CreateInstance(challengeType)!;
            challengeType.GetProperty("Settings")!.SetValue(challenge, Settings(system));
            challengeType.GetProperty("PlayerUid")!.SetValue(challenge, "waiting");
            ((IDictionary)Get(system, "_oauth2ByState"))["old"] = challenge;
            Reload(system, """{"Enabled":false}""");
            Require(speed == 1.75f && pending.Count == 0, "Disabling auth did not restore movement");
            Require(((IDictionary)Get(system, "_oauth2ByState")).Count == 0, "Old browser challenge survived reload");
            var identity = Activator.CreateInstance(SystemType.GetNestedType("OAuth2Identity", BindingFlags.NonPublic)!)!;
            Call(system, "CompleteOAuth2Auth", challenge, identity);
            Require(pending.Count == 0, "Old callback changed authentication state");
            Require(Get(system, "_listener") is null, "Disabled authentication left listener running");
            Reload(system, """{"Enabled":true}""");
            Require(speed == 0 && pending.Count == 1, "Enabling authentication skipped online players");
            Call(system, "Authenticate", player, "test", false, false);
            var oldMessageCount = messages.Count;
            Reload(system, """{"Enabled":true,"LoginTimeoutSeconds":90}""");
            Require(pending.Count == 0 && messages.Count == oldMessageCount, "Reload prompted already authenticated players");
            var handler = SystemType.GetMethod("CmdServerAuthAdmin", PrivateInstance)!;
            var commandArgs = new TextCommandCallingArgs
            {
                Parsers = new List<ICommandArgumentParser>
                {
                    Proxy.Make<ICommandArgumentParser>((method, _) => method.Name == "GetValue" ? "reload fixture" : Proxy.Default(method))
                }
            };
            var commandResult = (TextCommandResult)handler.Invoke(system, new object[] { commandArgs })!;
            Require(commandResult.Status == EnumCommandStatus.Success, "Reload command did not succeed");
            var activeSettings = Settings(system);
            File.WriteAllText(Path.Combine(GamePaths.ModConfig, "serverauth.json"), "{");
            commandResult = (TextCommandResult)handler.Invoke(system, new object[] { commandArgs })!;
            Require(commandResult.Status == EnumCommandStatus.Error && ReferenceEquals(activeSettings, Settings(system)), "Reload command did not report failure and preserve settings");
            Console.WriteLine("PASS auth reload: policy updates, malformed/missing configuration, listener start/rebind/rollback, pending deadline, movement restoration and stale callbacks.");
        }
        finally
        {
            system.Dispose();
            GamePaths.DataPath = originalPath;
            directory.Delete(true);
        }
    }
}

public class Proxy : DispatchProxy
{
    public System.Func<MethodInfo, object?[]?, object?> Handler { get; set; } = null!;
    public static T Make<T>(System.Func<MethodInfo, object?[]?, object?> handler) where T : class
    {
        var proxy = Create<T, Proxy>();
        ((Proxy)(object)proxy).Handler = handler;
        return proxy;
    }
    public static object? Default(MethodInfo method) => method.ReturnType == typeof(void) ? null :
        method.ReturnType.IsValueType ? Activator.CreateInstance(method.ReturnType) : null;
    protected override object? Invoke(MethodInfo? method, object?[]? args) => Handler(method!, args);
}
