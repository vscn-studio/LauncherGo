using Microsoft.Data.Sqlite;

namespace ServerMap.World;

internal static class SavedWorldQueries
{
    // Keyset pages release SQLite's read transaction between batches. Do not
    // use OFFSET or keep a full-table reader open while rendering/decoding.
    public static long[] MapPage(SqliteConnection connection, long? after, int size = 256)
    {
        using var command = connection.CreateCommand();
        command.CommandTimeout = 1;
        command.CommandText = after.HasValue
            ? "SELECT position FROM mapchunk WHERE position > @after ORDER BY position LIMIT @size"
            : "SELECT position FROM mapchunk ORDER BY position LIMIT @size";
        if (after.HasValue) command.Parameters.AddWithValue("@after", after.Value);
        command.Parameters.AddWithValue("@size", size);
        using var rows = command.ExecuteReader();
        var result = new List<long>();
        while (rows.Read()) result.Add(rows.GetInt64(0));
        return result.ToArray();
    }

    public static long[] ColumnPositions(SqliteConnection connection, IReadOnlyList<long> candidates)
    {
        if (candidates.Count == 0) return [];
        using var command = connection.CreateCommand();
        command.CommandTimeout = 1;
        var names = new string[candidates.Count];
        for (var i = 0; i < candidates.Count; i++)
        {
            names[i] = "@p" + i;
            command.Parameters.AddWithValue(names[i], candidates[i]);
        }
        command.CommandText = "SELECT position FROM chunk WHERE position IN (" + string.Join(",", names) + ")";
        using var rows = command.ExecuteReader();
        var result = new List<long>();
        while (rows.Read()) result.Add(rows.GetInt64(0));
        return result.ToArray();
    }
}
