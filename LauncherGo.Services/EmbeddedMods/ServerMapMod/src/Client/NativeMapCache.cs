using Microsoft.Data.Sqlite;
using ProtoBuf;
using ServerMap.Network;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

namespace ServerMap.Client;

/// <summary>Reads only the database selected by the active native map layer. No directory discovery, writes or world generation.</summary>
public static class NativeMapCache
{
    private const long CoordinateMask = (1L << 27) - 1;
    private const int MaxPieceBytes = 16 * 1024;

    public static IEnumerable<long[]> Read(string path, int chunksX, int chunksZ, CancellationToken cancellation)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Cache = SqliteCacheMode.Private, Pooling = false, DefaultTimeout = 1 }.ToString());
        connection.Open();
        long after = -1;
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            var cells = new List<long>(); var count = 0;
            using (var command = connection.CreateCommand())
            {
                // Close each reader before waiting for the network, so the scan never holds a long-lived read transaction.
                command.CommandText = "SELECT position, CASE WHEN length(data) BETWEEN 1 AND @maxBytes THEN data END FROM mappiece WHERE position > @after ORDER BY position LIMIT @limit";
                command.Parameters.AddWithValue("@after", after);
                command.Parameters.AddWithValue("@limit", MapExplorationProtocol.MaxBatch);
                command.Parameters.AddWithValue("@maxBytes", MaxPieceBytes);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    cancellation.ThrowIfCancellationRequested(); count++;
                    after = reader.GetInt64(0);
                    // FastVec2i.ToChunkIndex packs X in 27 low bits and Z in the next 27 bits, unlike our wire key.
                    if (after < 0 || after >> 54 != 0 || reader.IsDBNull(1)) continue;
                    var cell = MapExplorationProtocol.Cell((int)(after & CoordinateMask), (int)(after >> 27));
                    if (!MapExplorationProtocol.InWorld(cell, chunksX, chunksZ)) continue;
                    try
                    {
                        if (SerializerUtil.Deserialize<MapPieceDB>((byte[])reader.GetValue(1))?.Pixels is { Length: 1024 }) cells.Add(cell);
                    }
                    catch (Exception ex) when (ex is ProtoException or OverflowException or EndOfStreamException or InvalidDataException or ArgumentException)
                    { /* The native map cannot display these corrupt/invalid pieces either. */ }
                }
            }
            if (cells.Count > 0) yield return cells.ToArray();
            if (count < MapExplorationProtocol.MaxBatch) yield break;
        }
    }
}
