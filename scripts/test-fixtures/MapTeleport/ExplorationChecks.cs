using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using ServerMap.Client;
using ServerMap.Network;
using ServerMap.Web;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;
using Vintagestory.Server;

static class ExplorationChecks
{
    static void Require(bool ok, string reason) { if (!ok) throw new Exception(reason); }
    static T Empty<T>() => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
    static void Field(object instance, string name, object? value)
    {
        for (var type = instance.GetType(); type != null; type = type.BaseType)
            if (type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly) is { } field)
            { field.SetValue(instance, value); return; }
        throw new MissingFieldException(instance.GetType().Name, name);
    }
    static readonly MethodInfo NativeSend = typeof(ConnectedClient).GetMethod(nameof(ConnectedClient.SetChunkSent))!;

    public static void Run()
    {
        NativeClient(); NativeServerAndStore();
    }

    static void NativeClient()
    {
        var loaded = new[] { true, false }; var exists = true; var packets = new List<ClientMapExplorationPacket>();
        var logger = Proxy.Make<ILogger>();
        var chunks = Enumerable.Range(0, 2).Select(y => Proxy.Make<IClientChunk>((m, _) => m.Name switch
        { "get_LoadedFromServer" => loaded[y], "UnpackAndReadBlock" => 0, _ => Proxy.Default(m.ReturnType) })).ToArray();
        var mapChunk = Proxy.Make<IMapChunk>((m, _) => m.Name == "get_RainHeightMap" ? new ushort[1024] : Proxy.Default(m.ReturnType));
        var accessor = Proxy.Make<IBlockAccessor>((m, a) => m.Name switch
        {
            "GetChunk" => exists ? chunks[(int)a![1]!] : null,
            "GetMapChunk" => mapChunk,
            _ => Proxy.Default(m.ReturnType)
        });
        var world = Proxy.Make<IClientWorldAccessor>((m, _) => m.Name switch
        { "get_BlockAccessor" => accessor, "get_Blocks" => new List<Block> { new() { BlockMaterial = EnumBlockMaterial.Stone } }, _ => Proxy.Default(m.ReturnType) });
        var api = Proxy.Make<ICoreClientAPI>((m, _) => m.Name switch
        { "get_World" => world, "get_Logger" => logger, _ => Proxy.Default(m.ReturnType) });
        var channel = Proxy.Make<IClientNetworkChannel>((m, a) =>
        { if (m.Name == "SendPacket") packets.Add((ClientMapExplorationPacket)a![0]!); return Proxy.Default(m.ReturnType); });
        var layer = Empty<ChunkMapLayer>(); Field(layer, "api", api); Field(layer, "capi", api);
        Field(layer, "chunksTmp", new IWorldChunk[2]); Field(layer, "colors", new[] { 0x448844 });
        layer.block2Color = [0]; Field(layer, "readyMapPieces", new ConcurrentQueue<ReadyMapPiece>());
        var pos = new FastVec2i(12, 34);
        using var capture = new ClientNativeExploration(api); // Installs the actual Harmony patch against VSEssentials.dll.
        exists = false; Require(layer.GenerateChunkImage(pos, mapChunk) == null, "Native missing chunk should not generate a map piece");
        exists = true; Require(layer.GenerateChunkImage(pos, mapChunk) == null, "Native partially loaded column should not generate a map piece");
        var clientSession = capture.ClientSession;
        capture.Receive(new() { Session = new string('a', 32), ClientSession = clientSession }); capture.Send(channel, 0);
        Require(packets.Count == 0, "A failed native generation reported exploration");
        loaded[1] = true;
        var image = Task.Run(() => layer.GenerateChunkImage(pos, mapChunk)).GetAwaiter().GetResult();
        Require(image is { Length: 1024 } && packets.Count == 0, "Native background generation must only enqueue, never send on the render thread");
        capture.Send(channel, 10);
        Require(packets.Count == 1 && packets[0].Cells.SequenceEqual(new[] { MapExplorationProtocol.Cell(12, 34) }), "Native success did not report exactly one absolute chunk");
        capture.Receive(new() { Session = packets[0].Session, Sequence = packets[0].Sequence, Accepted = packets[0].Cells, ClientSession = clientSession });
        layer.GenerateChunkImage(pos, mapChunk); capture.Send(channel, 3000);
        Require(packets.Count == 1, "Native redraw caused duplicate exploration traffic");
        typeof(ChunkMapLayer).GetMethod("loadFromChunkPixels", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(layer, [new FastVec2i(90, 91), new int[1024]]);
        capture.Send(channel, 6000); Require(packets.Count == 1, "Historical/client-edited cache was imported without new native generation");
        // Keep capi for block access but use a foreign API identity, as a stale world/layer would.
        Field(layer, "api", Proxy.Make<ICoreClientAPI>((m, _) => m.Name == "get_World" ? world : Proxy.Default(m.ReturnType)));
        layer.GenerateChunkImage(new FastVec2i(13, 34), mapChunk); capture.Send(channel, 9000);
        Require(packets.Count == 1, "Another world's layer was accepted by the active capture");
        capture.Reset(); capture.Receive(new() { Session = new string('a', 32), ClientSession = clientSession });
        Require(!capture.Connected, "Delayed old hello reconnected a reset client to the wrong server session");
        Console.WriteLine("PASS native map generation: actual VSEssentials method + Harmony, missing/partial columns, off-thread success, exact coordinates, redraw dedupe and cache/world isolation");
    }

    static void NativeServerAndStore()
    {
        var root = Directory.CreateTempSubdirectory("LauncherGo-native-exploration-").FullName;
        try
        {
            var warnings = new List<string>();
            var logger = Proxy.Make<ILogger>((m, a) => { if (m.Name == "Warning") warnings.Add(a?[0]?.ToString() ?? "warning"); return Proxy.Default(m.ReturnType); });
            var server = Empty<ServerMain>(); server.Clients = new(); server.WorldMap = Empty<ServerWorldMap>();
            server.WorldMap.index3dMulX = server.WorldMap.index3dMulZ = 1024; server.WorldMap.chunkMapSizeY = 2;
            Field(server.WorldMap, "mapsize", new Vec3i(32768, 64, 32768));
            var api = Proxy.Make<ICoreServerAPI>((m, _) => m.Name switch { "get_World" => server, "get_Logger" => logger, _ => Proxy.Default(m.ReturnType) });
            ConnectedClient Connect(int id, string uid)
            {
                var client = Empty<ConnectedClient>(); client.Id = id; client.ChunkSent = []; client.MapChunkSent = [];
                var data = new ServerWorldPlayerData(); Field(data, "PlayerUID", uid); Field(data, "Entityplayer", new EntityPlayer());
                var player = Empty<ServerPlayer>(); Field(player, "worlddata", data); Field(player, "client", client);
                client.Player = player; client.WorldData = data;
                server.Clients.TryRemove(id, out _); server.Clients[id] = client;
                return client;
            }
            var alice = Connect(1, "alice"); var bob = Connect(2, "bob");
            using var store = new ExplorationStore(root);
            var map = Empty<ServerMapWebServer>(); Field(map, "exploration", store);
            using var sync = new ServerNativeExploration(api, () => map);
            void Send(ConnectedClient client, int x, int z, int height = 2, int dimension = 0)
            {
                for (var y = 0; y < height; y++) NativeSend.Invoke(client, [server.WorldMap.ChunkIndex3D(x, y, z, dimension)]);
            }
            // Initial terrain can arrive before the custom ready handshake.
            Send(alice, 12, 34); Send(alice, 13, 34, 1); Send(bob, 14, 34); Send(alice, 15, 34, dimension: 1);
            var clientSession = new string('a', 32);
            Require(sync.Begin(alice.Player, 0, clientSession) == null, "Old clients must not gain a radius fallback");
            Require(sync.Begin(alice.Player, MapExplorationProtocol.Version, "") == null, "Missing client session was accepted");
            var hello = sync.Begin(alice.Player, MapExplorationProtocol.Version, clientSession)!;
            Require(sync.Begin(alice.Player, MapExplorationProtocol.Version, clientSession)!.Session == hello.Session, "Repeated hello replaced the connection nonce");
            var now = 1000L;
            ServerMapExplorationAckPacket? Report(IServerPlayer player, string token, long seq, params long[] cells)
            { now += 500; return sync.Receive(player, new() { Session = token, Sequence = seq, Cells = cells }, now); }
            store.UpdateGroups(alice.Player);
            Require(store.VisibleCells("alice", 0, 0, 32000, 32000, false, []).Length == 0, "Player/group ticks still reveal a position-radius window");
            // Simulate a rapid teleport/unload before the background client's report is processed.
            alice.ChunkSent.Clear();
            var ack = Report(alice.Player, hello.Session, 1, MapExplorationProtocol.Cell(12, 34), MapExplorationProtocol.Cell(13, 34),
                MapExplorationProtocol.Cell(14, 34), MapExplorationProtocol.Cell(15, 34), MapExplorationProtocol.Cell(-1, 0), MapExplorationProtocol.Cell(1024, 0))!;
            Require(ack.Accepted.SequenceEqual(new[] { MapExplorationProtocol.Cell(12, 34) }), "Delivery proof accepted partial/other-player/other-dimension/out-of-world columns or lost delayed native success");
            Require(store.IsVisible("alice", 384, 1088, false, []) && store.IsVisible("alice", 415.99, 1119.99, false, [])
                && !store.IsVisible("alice", 383.99, 1088, false, []) && !store.IsVisible("alice", 416, 1088, false, []), "Exploration did not preserve exact 32x32 half-open pixels");
            Require(!store.IsVisible("bob", 384, 1088, false, []), "Alice's native generation leaked into Bob's exploration");
            Send(alice, 13, 34);
            ack = Report(alice.Player, hello.Session, 2, MapExplorationProtocol.Cell(13, 34))!;
            Require(ack.Accepted.Length == 1, "Completing the actual column did not unlock subsequent native success");
            using (var reloaded = new ExplorationStore(root)) Require(reloaded.ContainsOwnCell("alice", ack.Accepted[0]), "ACK was sent before persistence");
            var version = store.Version("alice"); var persistencePath = Path.Combine(root, "exploration.json");
            var original = File.ReadAllText(persistencePath);
            Send(alice, 16, 34); File.Move(persistencePath, persistencePath + ".backup"); Directory.CreateDirectory(persistencePath);
            try
            {
                Report(alice.Player, hello.Session, 3, MapExplorationProtocol.Cell(16, 34));
                throw new Exception("Unwritable exploration store was acknowledged");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            Require(store.Version("alice") == version && !store.IsVisible("alice", 512, 1088, false, []), "Failed save enabled in-memory fog/teleport permission");
            Directory.Delete(persistencePath); File.Move(persistencePath + ".backup", persistencePath);
            Require(File.ReadAllText(persistencePath) == original, "Failed save changed previous exploration");
            ack = Report(alice.Player, hello.Session, 3, MapExplorationProtocol.Cell(16, 34))!;
            Require(ack.Accepted.Length == 1 && store.Version("alice") == version + 1, "Native batch could not retry a failed durable save");
            Send(alice, 17, 34); sync.Forget(alice.Player); var replacement = Connect(1, "alice");
            var next = sync.Begin(replacement.Player, MapExplorationProtocol.Version, new string('b', 32))!;
            Require(next.Session != hello.Session && Report(replacement.Player, hello.Session, 1, MapExplorationProtocol.Cell(17, 34)) == null,
                "Old connection token survived reconnect");
            Require(Report(alice.Player, hello.Session, 4, MapExplorationProtocol.Cell(17, 34)) == null, "Disconnected player object could still authorize reports");
            Require(Report(replacement.Player, next.Session, 1, MapExplorationProtocol.Cell(17, 34))!.Accepted.Length == 0,
                "Unacknowledged old-session delivery was trusted as new exploration");
            Require(store.ContainsOwnCell("alice", MapExplorationProtocol.Cell(12, 34)), "Reconnect lost permanent exploration");
            store.Dispose();
            try { Report(replacement.Player, next.Session, 2, MapExplorationProtocol.Cell(12, 34)); throw new Exception("Disposed store was acknowledged during shutdown"); }
            catch (ObjectDisposedException) { }
            Require(warnings.Count == 0, "Native delivery hook failed: " + string.Join("; ", warnings));
            Console.WriteLine("PASS native server delivery: actual SetChunkSent patch, pre-hello history, complete columns, delayed teleport/unload, player/dimension/world bounds, durable ACK, failed-save rollback/retry and reconnect isolation");
        }
        finally { Directory.Delete(root, true); }
    }
}
