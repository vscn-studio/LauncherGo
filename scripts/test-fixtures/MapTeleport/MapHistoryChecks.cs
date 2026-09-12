using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using ServerMap.Client;
using ServerMap.Network;
using ServerMap.Web;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;
using Vintagestory.Server;

static class MapHistoryChecks
{
    static void Require(bool ok, string reason) { if (!ok) throw new Exception(reason); }
    static T Empty<T>() => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
    static void Field(object instance, string name, object? value)
    {
        for (var type = instance.GetType(); type != null; type = type.BaseType)
            if (type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly) is { } field)
            { field.SetValue(instance, value); return; }
        if (instance.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) is { CanWrite: true } property)
        { property.SetValue(instance, value); return; }
        throw new MissingFieldException(instance.GetType().Name, name);
    }
    static long Cell(int x, int z = 0) => MapExplorationProtocol.Cell(x, z);
    static MapPieceDB Piece() => new() { Pixels = Enumerable.Repeat(unchecked((int)0xff448844), 1024).ToArray() };
    static byte[] HashOpenFile(string file)
    {
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return SHA256.HashData(stream);
    }

    public static void Run()
    {
        NativeCacheAndClient(); NativeServerReplacement();
    }

    static void NativeCacheAndClient()
    {
        var root = Directory.CreateTempSubdirectory("LauncherGo-map-history-cache-").FullName;
        try
        {
            var logger = Proxy.Make<ILogger>(); var file = Path.Combine(root, "world-a.db");
            using var db = new MapDB(logger); string error = null!;
            Require(db.OpenOrCreate(file, ref error, true, true, false) && error == null, "Could not create native map fixture");
            var pieces = Enumerable.Range(0, 300).ToDictionary(x => new FastVec2i(x, 400), _ => Piece());
            pieces[new(1001, 1000)] = Piece();
            pieces[new(1, 2)] = new() { Pixels = null! }; pieces[new(2, 2)] = new() { Pixels = [1] };
            pieces[new(2000, 0)] = Piece();
            db.SetMapPieces(pieces);
            using (var other = new MapDB(logger))
            {
                Require(other.OpenOrCreate(Path.Combine(root, "world-b.db"), ref error, true, true, false), "Other world fixture failed");
                other.SetMapPieces(new() { [new(999, 999)] = Piece() });
            }
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = file }.ToString()))
            {
                connection.Open(); using var command = connection.CreateCommand();
                command.CommandText = "INSERT INTO mappiece(position,data) VALUES(-1,X'FF'),(3,X'FF'),(4,zeroblob(20000))";
                command.ExecuteNonQuery();
            }
            var before = HashOpenFile(file);
            var scanned = NativeMapCache.Read(file, 1024, 1024, CancellationToken.None).SelectMany(p => p).ToHashSet();
            Require(scanned.Count == 301 && scanned.Contains(Cell(1001, 1000)) && scanned.Contains(Cell(0, 400)), "Native 27-bit database keys were decoded incorrectly");
            Require(!scanned.Contains(Cell(999, 999)) && !scanned.Contains(Cell(1, 2)) && !scanned.Contains(Cell(2000)), "Other world, corrupt, blank or out-of-world pieces were imported");
            Require(before.SequenceEqual(HashOpenFile(file)), "Historical scan wrote to the native map database");
            using (var scan = NativeMapCache.Read(file, 1024, 1024, CancellationToken.None).GetEnumerator())
            {
                Require(scan.MoveNext(), "Paged cache scan did not start");
                db.SetMapPieces(new() { [new(900, 500)] = Piece() }); // Must not be blocked by a reader waiting on network ACKs.
                var all = scan.Current.ToHashSet(); while (scan.MoveNext()) all.UnionWith(scan.Current);
                Require(all.Contains(Cell(900, 500)), "Keyset scan lost a newly saved later piece");
            }
            var missing = Path.Combine(root, "missing.db");
            try { NativeMapCache.Read(missing, 1024, 1024, CancellationToken.None).ToArray(); throw new Exception("Missing cache should fail, not complete an empty snapshot"); }
            catch (SqliteException) { Require(!File.Exists(missing), "Read-only scan created a missing database"); }
            using (var cancel = new CancellationTokenSource())
            {
                cancel.Cancel();
                try { NativeMapCache.Read(file, 1024, 1024, cancel.Token).ToArray(); throw new Exception("Cancelled scan completed"); }
                catch (OperationCanceledException) { }
            }

            // Exercise the real client coordinator against the native layer's selected DB; no game map dragging is needed.
            var manager = Empty<WorldMapManager>(); var layer = Empty<ChunkMapLayer>(); manager.MapLayers = [layer];
            var player = Proxy.Make<IClientPlayer>((m, _) => m.Name == "get_Entity" ? new EntityPlayer() : Proxy.Default(m.ReturnType));
            var accessor = Proxy.Make<IBlockAccessor>((m, _) => m.Name is "get_MapSizeX" or "get_MapSizeZ" ? 32768 : Proxy.Default(m.ReturnType));
            var world = Proxy.Make<IClientWorldAccessor>((m, _) => m.Name switch
            { "get_SavegameIdentifier" => "world-a", "get_Player" => player, "get_BlockAccessor" => accessor, _ => Proxy.Default(m.ReturnType) });
            var loader = Proxy.Make<IModLoader>((m, _) => m.Name == "GetModSystem" ? manager : Proxy.Default(m.ReturnType));
            var api = Proxy.Make<ICoreClientAPI>((m, _) => m.Name switch
            { "get_World" => world, "get_Logger" => logger, "get_ModLoader" => loader, _ => Proxy.Default(m.ReturnType) });
            Field(layer, "api", api); Field(layer, "mapdb", db);
            var sent = new List<ClientMapHistoryPacket>();
            var channel = Proxy.Make<IClientNetworkChannel>((m, a) =>
            { if (m.Name == "SendPacket" && a![0] is ClientMapHistoryPacket packet) sent.Add(packet); return Proxy.Default(m.ReturnType); });
            using var client = new ClientNativeExploration(api);
            var clientSession = client.ClientSession; var token = new string('a', 32);
            client.Receive(new ServerMapExplorationAckPacket { Session = token, ClientSession = clientSession, HistoryProtocol = MapHistoryProtocol.Version, WorldId = "world-a" });
            var deadline = DateTime.UtcNow.AddSeconds(10); long now = 0; var completed = false;
            while (!completed && DateTime.UtcNow < deadline)
            {
                var count = sent.Count; client.Send(channel, now);
                if (sent.Count == count) { Thread.Sleep(1); continue; }
                var packet = sent[^1];
                client.ReceiveHistory(new ServerMapHistoryAckPacket { Session = token, ClientSession = clientSession, Snapshot = packet.Snapshot, Sequence = packet.Sequence, Complete = packet.Complete });
                completed = packet.Complete; now += 500;
            }
            Require(completed && sent.Where(p => !p.Complete).SelectMany(p => p.Cells).Distinct().Count() == 302, "Joining did not automatically synchronize the entire native cache");
            client.Reset(); var countBefore = sent.Count;
            client.Receive(new ServerMapExplorationAckPacket { Session = token, ClientSession = clientSession, HistoryProtocol = 1, WorldId = "world-a" });
            client.Send(channel, now + 10000); Require(!client.Connected && sent.Count == countBefore, "Old hello revived a previous world's history scan");
            client.Receive(new ServerMapExplorationAckPacket { Session = new string('b', 32), ClientSession = client.ClientSession, HistoryProtocol = 1, WorldId = "world-b" });
            client.Send(channel, now + 20000); Require(sent.Count == countBefore, "Foreign world identity authorized native cache upload");
            Console.WriteLine("PASS native map history: real MapDB format, read-only paged full cache, world isolation, invalid data, live writes, automatic client upload and stale-world cancellation");
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    static void NativeServerReplacement()
    {
        var root = Directory.CreateTempSubdirectory("LauncherGo-map-history-server-").FullName;
        try
        {
            var server = Empty<ServerMain>(); server.Clients = new(); server.WorldMap = Empty<ServerWorldMap>();
            server.WorldMap.index3dMulX = server.WorldMap.index3dMulZ = 1024; server.WorldMap.chunkMapSizeY = 2;
            Field(server.WorldMap, "mapsize", new Vec3i(32768, 64, 32768));
            var saveField = typeof(ServerMain).GetField("SaveGameData", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!;
            var save = RuntimeHelpers.GetUninitializedObject(saveField.FieldType); Field(save, "SavegameIdentifier", "world-a"); saveField.SetValue(server, save);
            var api = Proxy.Make<Vintagestory.API.Server.ICoreServerAPI>((m, _) => m.Name switch
            { "get_World" => server, "get_Logger" => Proxy.Make<ILogger>(), _ => Proxy.Default(m.ReturnType) });
            ConnectedClient Connect(int id, string uid)
            {
                var connection = Empty<ConnectedClient>(); connection.Id = id; connection.ChunkSent = []; connection.MapChunkSent = [];
                var data = new ServerWorldPlayerData(); Field(data, "PlayerUID", uid); Field(data, "Entityplayer", new EntityPlayer());
                var player = Empty<ServerPlayer>(); Field(player, "worlddata", data); Field(player, "client", connection);
                connection.Player = player; connection.WorldData = data; server.Clients[id] = connection; return connection;
            }
            var alice = Connect(1, "alice"); var bob = Connect(2, "bob");
            using var store = new ExplorationStore(root); store.RecordNativeChunks(alice.Player, [Cell(99)]); store.RecordNativeChunks(bob.Player, [Cell(98)]);
            var web = Empty<ServerMapWebServer>(); Field(web, "exploration", store);
            using var sync = new ServerNativeExploration(api, () => web);
            var hello = sync.Begin(alice.Player, MapExplorationProtocol.Version, new string('a', 32), MapHistoryProtocol.Version, "world-a")!;
            Require(hello.HistoryProtocol == MapHistoryProtocol.Version && hello.WorldId == "world-a", "History capability/world negotiation failed");
            var wrong = sync.Begin(bob.Player, MapExplorationProtocol.Version, new string('b', 32), MapHistoryProtocol.Version, "world-b")!;
            Require(wrong.HistoryProtocol == 0, "A different world was allowed to replace exploration");
            var page = new ClientMapHistoryPacket { Session = hello.Session, WorldId = "world-a", Snapshot = new string('c', 32), Sequence = 1, Cells = [Cell(4), Cell(700, 800)] };
            Require(sync.ReceiveHistory(bob.Player, page, 0) == null, "A foreign player could upload another player's snapshot");
            Require(sync.ReceiveHistory(alice.Player, page, 0) is { Complete: false }, "Historical chunks required current-connection delivery proof");
            Require(store.ContainsOwnCell("alice", Cell(99)) && !store.ContainsOwnCell("alice", Cell(4)), "Incomplete history changed visibility or teleport permission");
            var version = store.Version("alice"); var original = File.ReadAllText(Path.Combine(root, "exploration.json"));
            var finish = new ClientMapHistoryPacket { Session = hello.Session, WorldId = "world-a", Snapshot = page.Snapshot, Sequence = 2, Complete = true };
            var persistence = Path.Combine(root, "exploration.json"); File.Move(persistence, persistence + ".backup"); Directory.CreateDirectory(persistence);
            try { sync.ReceiveHistory(alice.Player, finish, 500); throw new Exception("Failed replacement was acknowledged"); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            Require(store.Version("alice") == version && store.ContainsOwnCell("alice", Cell(99)) && !store.ContainsOwnCell("alice", Cell(4)), "Failed history save changed in-memory permissions");
            Directory.Delete(persistence); File.Move(persistence + ".backup", persistence);
            Require(File.ReadAllText(persistence) == original, "Failed replacement destroyed old exploration");
            Require(sync.ReceiveHistory(alice.Player, finish, 2500) is { Complete: true }, "Durable history completion was not acknowledged");
            Require(!store.ContainsOwnCell("alice", Cell(99)) && store.ContainsOwnCell("alice", Cell(700, 800)) && store.ContainsOwnCell("bob", Cell(98)), "Full history failed to correct old coverage or changed another player");
            using (var restored = new ExplorationStore(root)) Require(restored.ContainsOwnCell("alice", Cell(4)) && !restored.ContainsOwnCell("alice", Cell(99)), "Final ACK preceded durable replacement");
            Require((int)typeof(ServerMapWebServer).GetField("explorationGroupsChanged", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(web)! == 1, "Shrinking exploration did not request privacy/teleport invalidation");
            sync.Forget(alice.Player); var next = Connect(1, "alice"); sync.Begin(next.Player, MapExplorationProtocol.Version, new string('d', 32), MapHistoryProtocol.Version, "world-a");
            Require(sync.ReceiveHistory(next.Player, finish, 3000) == null && sync.ReceiveHistory(alice.Player, finish, 3500) == null, "Old snapshot survived disconnect/reconnect");
            Console.WriteLine("PASS historical replacement: real server identity, trusted old cache, partial buffering, atomic save rollback/retry, durable final ACK, old-radius correction, privacy invalidation and reconnect isolation");
        }
        finally { Directory.Delete(root, true); }
    }
}
