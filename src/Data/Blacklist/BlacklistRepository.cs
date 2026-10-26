using Microsoft.Data.Sqlite;

namespace SpankBank.Data.Blacklist;

/// <summary>The blacklist entries and the posts they currently block (SpankBank.sqlite). The worker fills BlockedPosts.</summary>
public sealed class BlacklistRepository
{
    private readonly string _connectionString;

    public BlacklistRepository(string appDatabasePath) => _connectionString = $"Data Source={appDatabasePath}";

    public IReadOnlyList<string> GetAll()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT Query FROM Blacklist ORDER BY Id;";
        using var reader = cmd.ExecuteReader();
        var entries = new List<string>();
        while (reader.Read()) entries.Add(reader.GetString(0));
        return entries;
    }

    /// <summary>Replaces the whole list. Lines are trimmed, blank ones dropped, repeats (ignoring case) collapsed.</summary>
    /// <returns>The first problem found as "line: message", or null when saved.</returns>
    public string? SetAll(IEnumerable<string> lines)
    {
        var entries = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in lines)
        {
            var line = string.Join(' ', raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            if (line.Length == 0 || !seen.Add(line)) continue;
            if (BlacklistRules.Validate(line) is { } problem) return $"\"{line}\": {problem}";
            entries.Add(line);
        }

        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var transaction = connection.BeginTransaction();
        using (var clear = connection.CreateCommand())
        {
            clear.Transaction = transaction;
            clear.CommandText = "DELETE FROM Blacklist;";
            clear.ExecuteNonQuery();
        }
        foreach (var entry in entries)
        {
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO Blacklist (Query, CreatedAt) VALUES ($query, $now);";
            insert.Parameters.AddWithValue("$query", entry);
            insert.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("o"));
            insert.ExecuteNonQuery();
        }
        transaction.Commit();
        MarkDirty();
        return null;
    }

    /// <summary>Asks the worker to recompute which posts are blocked (after the list or a watch's override changed).</summary>
    public void MarkDirty()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO Settings (Key, Value) VALUES ('Blacklist.Dirty', 'true')
            ON CONFLICT(Key) DO UPDATE SET Value = 'true';
            """;
        cmd.ExecuteNonQuery();
    }

    /// <summary>Blocked posts that are not on this device yet (never downloaded) and ones already downloaded.</summary>
    public (int Pending, int Local) CountBlocked(string deviceDatabasePath)
    {
        using var connection = new SqliteConnection($"{_connectionString};Pooling=False");
        connection.Open();
        using (var attach = connection.CreateCommand())
        {
            attach.CommandText = $"ATTACH DATABASE '{deviceDatabasePath.Replace("'", "''")}' AS dev;";
            attach.ExecuteNonQuery();
        }
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT COALESCE(SUM(CASE WHEN d.Id IS NULL THEN 1 ELSE 0 END), 0), COALESCE(SUM(CASE WHEN d.Id IS NULL THEN 0 ELSE 1 END), 0)
            FROM BlockedPosts b LEFT JOIN dev.Posts d ON d.Id = b.PostId AND d.SavedAt IS NOT NULL;
            """;
        using var reader = cmd.ExecuteReader();
        reader.Read();
        return (reader.GetInt32(0), reader.GetInt32(1));
    }
}
