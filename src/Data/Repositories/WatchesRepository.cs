using Microsoft.Data.Sqlite;
using SpankBank.Core.Models;

namespace SpankBank.Data.Repositories;

public sealed record WatchedArtistTag(long Id, string Tag, ArtistTagKind Kind, bool IsManual, bool Enabled);

/// <summary>Pool details from the imported catalog; null on a WatchEntry when the pool is not in the export yet.</summary>
public sealed record PoolInfo(string Name, string Category, int PostCount);

public sealed record WatchEntry(
    long Id,
    WatchedItemType Type,
    string SourceValue,
    string DisplayName,
    bool Enabled,
    IReadOnlyList<WatchedArtistTag> ArtistTags,
    string? LocalName = null,
    PoolInfo? Pool = null,
    int MatchCount = 0,
    bool Matched = false,
    string? Error = null)
{
    /// <summary>"1,234" once the worker has matched this watch, "pending" until then.</summary>
    public string MatchText => Matched ? MatchCount.ToString("N0") : "pending";
}

/// <summary>Watched artists, pools and searches (SpankBank.sqlite). Artist alias/character discovery reads the catalog.</summary>
public sealed class WatchesRepository
{
    private readonly string _appConnectionString;
    private readonly string _serverConnectionString;

    public WatchesRepository(string appDatabasePath, string serverDatabasePath)
    {
        _appConnectionString = $"Data Source={appDatabasePath}";
        _serverConnectionString = $"Data Source={serverDatabasePath};Mode=ReadOnly";
    }

    public IReadOnlyList<WatchEntry> GetAll(WatchedItemType type)
    {
        using var connection = new SqliteConnection(_appConnectionString);
        connection.Open();

        var entries = new List<(long Id, string Source, string Display, bool Enabled, string? LocalName, int Count, bool Matched, string? Error)>();
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT Id, SourceValue, DisplayName, Enabled, LocalName, MatchCount, LastCheckedAt IS NOT NULL, ErrorMessage FROM WatchedItems WHERE ItemType = $type ORDER BY SourceValue COLLATE NOCASE;";
            cmd.Parameters.AddWithValue("$type", (int)type);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                entries.Add((reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3) != 0, reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.GetInt32(5), reader.GetInt32(6) != 0, reader.IsDBNull(7) ? null : reader.GetString(7)));
        }

        var tagsByItem = new Dictionary<long, List<WatchedArtistTag>>();
        if (type == WatchedItemType.Artist)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                SELECT t.WatchedItemId, t.Id, t.Tag, t.Kind, t.IsManual, t.Enabled
                FROM WatchedArtistTags t
                JOIN WatchedItems w ON w.Id = t.WatchedItemId
                WHERE w.ItemType = $type
                ORDER BY t.Kind, t.Tag COLLATE NOCASE;
                """;
            cmd.Parameters.AddWithValue("$type", (int)type);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var itemId = reader.GetInt64(0);
                if (!tagsByItem.TryGetValue(itemId, out var list))
                    tagsByItem[itemId] = list = [];
                list.Add(new WatchedArtistTag(reader.GetInt64(1), reader.GetString(2), (ArtistTagKind)reader.GetInt32(3),
                    reader.GetInt32(4) != 0, reader.GetInt32(5) != 0));
            }
        }

        var pools = type == WatchedItemType.Pool ? LoadPools(entries.Select(e => e.Source)) : [];

        return entries
            .Select(e =>
            {
                PoolInfo? pool = long.TryParse(e.Source, out var poolId) && pools.TryGetValue(poolId, out var info) ? info : null;
                var display = !string.IsNullOrWhiteSpace(e.LocalName) ? e.LocalName
                    : pool is not null ? pool.Name.Replace('_', ' ')
                    : e.Display;
                return new WatchEntry(e.Id, type, e.Source, display, e.Enabled,
                    tagsByItem.TryGetValue(e.Id, out var tags) ? tags : [], e.LocalName, pool, e.Count, e.Matched, e.Error);
            })
            .ToList();
    }

    private Dictionary<long, PoolInfo> LoadPools(IEnumerable<string> ids)
    {
        var wanted = ids.Select(i => long.TryParse(i, out var id) ? id : (long?)null).Where(i => i is not null).Select(i => i!.Value).ToList();
        var result = new Dictionary<long, PoolInfo>();
        if (wanted.Count == 0) return result;

        using var connection = new SqliteConnection(_serverConnectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        var placeholders = wanted.Select((_, i) => $"$id{i}").ToList();
        cmd.CommandText = $"SELECT Id, Name, Category, PostCount FROM Pools WHERE Id IN ({string.Join(", ", placeholders)});";
        for (var i = 0; i < wanted.Count; i++) cmd.Parameters.AddWithValue(placeholders[i], wanted[i]);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            result[reader.GetInt64(0)] = new PoolInfo(reader.GetString(1), reader.GetString(2), reader.GetInt32(3));
        return result;
    }

    /// <summary>Sets the local-only name of an entry. An empty name goes back to the catalog name.</summary>
    public void SetLocalName(long id, string? name)
    {
        using var connection = new SqliteConnection(_appConnectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE WatchedItems SET LocalName = $name WHERE Id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$name", string.IsNullOrWhiteSpace(name) ? DBNull.Value : name.Trim());
        cmd.ExecuteNonQuery();
    }

    public bool IsCharacterTag(string name)
    {
        using var connection = new SqliteConnection(_serverConnectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM Tags WHERE Name = $name AND Category = 4 LIMIT 1;";
        cmd.Parameters.AddWithValue("$name", name);
        return cmd.ExecuteScalar() is not null;
    }

    /// <summary>Adds a watch. Returns null when the same source is already watched.</summary>
    public long? Add(WatchedItemType type, string sourceValue, string displayName)
    {
        using var connection = new SqliteConnection(_appConnectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO WatchedItems (ItemType, SourceValue, DisplayName, Enabled, AutoDownload)
            SELECT $type, $source, $display, 1, 1
            WHERE NOT EXISTS (SELECT 1 FROM WatchedItems WHERE ItemType = $type AND SourceValue = $source COLLATE NOCASE);
            """;
        cmd.Parameters.AddWithValue("$type", (int)type);
        cmd.Parameters.AddWithValue("$source", sourceValue);
        cmd.Parameters.AddWithValue("$display", displayName);
        if (cmd.ExecuteNonQuery() == 0) return null;

        cmd.CommandText = "SELECT last_insert_rowid();";
        var id = (long)cmd.ExecuteScalar()!;
        RequestMatch();
        return id;
    }

    public void SetEnabled(long id, bool enabled)
    {
        Execute("UPDATE WatchedItems SET Enabled = $v WHERE Id = $id;", id, enabled ? 1 : 0);
        MarkChanged(id);
    }

    public void Delete(long id)
    {
        using var connection = new SqliteConnection(_appConnectionString);
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys = ON;";
        pragma.ExecuteNonQuery();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM WatchedItems WHERE Id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public void SetArtistTagEnabled(long tagRowId, bool enabled)
    {
        Execute("UPDATE WatchedArtistTags SET Enabled = $v WHERE Id = $id;", tagRowId, enabled ? 1 : 0);
        MarkArtistOfTagChanged(tagRowId);
    }

    public void DeleteArtistTag(long tagRowId)
    {
        MarkArtistOfTagChanged(tagRowId);
        Execute("DELETE FROM WatchedArtistTags WHERE Id = $id;", tagRowId, 0);
    }

    /// <summary>Flags a watch for re-matching and asks the worker to do it.</summary>
    private void MarkChanged(long watchId)
    {
        Execute("UPDATE WatchedItems SET LastCheckedAt = NULL WHERE Id = $id;", watchId, 0);
        RequestMatch();
    }

    private void MarkArtistOfTagChanged(long tagRowId)
    {
        Execute("UPDATE WatchedItems SET LastCheckedAt = NULL WHERE Id = (SELECT WatchedItemId FROM WatchedArtistTags WHERE Id = $id);", tagRowId, 0);
        RequestMatch();
    }

    private void RequestMatch()
    {
        using var connection = new SqliteConnection(_appConnectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO Settings (Key, Value) VALUES ('Worker.MatchRequested', 'true')
            ON CONFLICT(Key) DO UPDATE SET Value = 'true';
            """;
        cmd.ExecuteNonQuery();
    }

    public void AddArtistTag(long watchedItemId, string tag, ArtistTagKind kind, bool isManual)
    {
        using var connection = new SqliteConnection(_appConnectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO WatchedArtistTags (WatchedItemId, Tag, Kind, IsManual, Enabled)
            VALUES ($item, $tag, $kind, $manual, 1);
            """;
        cmd.Parameters.AddWithValue("$item", watchedItemId);
        cmd.Parameters.AddWithValue("$tag", tag);
        cmd.Parameters.AddWithValue("$kind", (int)kind);
        cmd.Parameters.AddWithValue("$manual", isManual ? 1 : 0);
        if (cmd.ExecuteNonQuery() > 0) MarkChanged(watchedItemId);
    }

    /// <summary>Adds newly discovered aliases and characters; existing rows (and their toggles) are kept.</summary>
    public void RefreshArtist(long watchedItemId, string artistTag)
    {
        var aliases = FindAliases(artistTag);
        foreach (var alias in aliases)
            AddArtistTag(watchedItemId, alias, ArtistTagKind.Alias, isManual: false);
        foreach (var character in FindCharacters([artistTag, .. aliases]))
            AddArtistTag(watchedItemId, character, ArtistTagKind.Character, isManual: false);
    }

    public IReadOnlyList<string> FindAliases(string artistTag)
    {
        using var connection = new SqliteConnection(_serverConnectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT AntecedentName FROM TagAliases
            WHERE ConsequentName = $name AND Status = 'active'
            ORDER BY AntecedentName COLLATE NOCASE;
            """;
        cmd.Parameters.AddWithValue("$name", artistTag);
        return ReadStrings(cmd);
    }

    /// <summary>Character tags ending in "_(name)" for each artist name or alias.</summary>
    public IReadOnlyList<string> FindCharacters(IEnumerable<string> artistNames)
    {
        using var connection = new SqliteConnection(_serverConnectionString);
        connection.Open();
        var found = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in artistNames)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                SELECT Name FROM Tags
                WHERE Category = 4 AND Name LIKE $pattern ESCAPE '\'
                ORDER BY Name;
                """;
            cmd.Parameters.AddWithValue("$pattern", "%\\_(" + EscapeLike(name) + ")");
            foreach (var tag in ReadStrings(cmd)) found.Add(tag);
        }
        return found.ToList();
    }

    /// <summary>Resolves an alias to its artist tag when the name is an active alias.</summary>
    public string ResolveArtistTag(string input)
    {
        var name = Normalize(input);
        using var connection = new SqliteConnection(_serverConnectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT ConsequentName FROM TagAliases WHERE AntecedentName = $name AND Status = 'active' LIMIT 1;";
        cmd.Parameters.AddWithValue("$name", name);
        return cmd.ExecuteScalar() as string ?? name;
    }

    public bool IsArtistTag(string name)
    {
        using var connection = new SqliteConnection(_serverConnectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM Tags WHERE Name = $name AND Category = 1 LIMIT 1;";
        cmd.Parameters.AddWithValue("$name", name);
        return cmd.ExecuteScalar() is not null;
    }

    public static string Normalize(string tag) => tag.Trim().ToLowerInvariant().Replace(' ', '_');

    private static string EscapeLike(string value) => value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    private static List<string> ReadStrings(SqliteCommand cmd)
    {
        using var reader = cmd.ExecuteReader();
        var results = new List<string>();
        while (reader.Read()) results.Add(reader.GetString(0));
        return results;
    }

    private void Execute(string sql, long id, int value)
    {
        using var connection = new SqliteConnection(_appConnectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$v", value);
        cmd.ExecuteNonQuery();
    }
}
