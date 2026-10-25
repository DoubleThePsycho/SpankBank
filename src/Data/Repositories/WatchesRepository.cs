using Microsoft.Data.Sqlite;
using SpankBank.Core.Models;

namespace SpankBank.Data.Repositories;

public sealed record WatchedArtistTag(long Id, string Tag, ArtistTagKind Kind, bool IsManual, bool Enabled);

public sealed record WatchEntry(
    long Id,
    WatchedItemType Type,
    string SourceValue,
    string DisplayName,
    bool Enabled,
    IReadOnlyList<WatchedArtistTag> ArtistTags);

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

        var entries = new List<(long Id, string Source, string Display, bool Enabled)>();
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT Id, SourceValue, DisplayName, Enabled FROM WatchedItems WHERE ItemType = $type ORDER BY SourceValue COLLATE NOCASE;";
            cmd.Parameters.AddWithValue("$type", (int)type);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                entries.Add((reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3) != 0));
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

        return entries
            .Select(e => new WatchEntry(e.Id, type, e.Source, e.Display, e.Enabled,
                tagsByItem.TryGetValue(e.Id, out var tags) ? tags : []))
            .ToList();
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
        return (long)cmd.ExecuteScalar()!;
    }

    public void SetEnabled(long id, bool enabled) => Execute("UPDATE WatchedItems SET Enabled = $v WHERE Id = $id;", id, enabled ? 1 : 0);

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

    public void SetArtistTagEnabled(long tagRowId, bool enabled) => Execute("UPDATE WatchedArtistTags SET Enabled = $v WHERE Id = $id;", tagRowId, enabled ? 1 : 0);

    public void DeleteArtistTag(long tagRowId) => Execute("DELETE FROM WatchedArtistTags WHERE Id = $id;", tagRowId, 0);

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
        cmd.ExecuteNonQuery();
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

    /// <summary>Artist tags (and artists reachable through an alias) starting with the given text.</summary>
    public IReadOnlyList<string> SuggestArtists(string term, int limit = 10)
    {
        if (string.IsNullOrWhiteSpace(term)) return [];
        var prefix = EscapeLike(Normalize(term)) + "%";

        using var connection = new SqliteConnection(_serverConnectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT Name FROM Tags
            WHERE Category = 1 AND Name LIKE $prefix ESCAPE '\'
            ORDER BY PostCount DESC
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$prefix", prefix);
        cmd.Parameters.AddWithValue("$limit", limit);
        var results = ReadStrings(cmd).ToList();

        if (results.Count < limit)
        {
            using var aliasCmd = connection.CreateCommand();
            aliasCmd.CommandText = """
                SELECT DISTINCT a.ConsequentName FROM TagAliases a
                JOIN Tags t ON t.Name = a.ConsequentName AND t.Category = 1
                WHERE a.Status = 'active' AND a.AntecedentName LIKE $prefix ESCAPE '\'
                LIMIT $limit;
                """;
            aliasCmd.Parameters.AddWithValue("$prefix", prefix);
            aliasCmd.Parameters.AddWithValue("$limit", limit);
            foreach (var name in ReadStrings(aliasCmd))
                if (!results.Contains(name) && results.Count < limit) results.Add(name);
        }
        return results;
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
