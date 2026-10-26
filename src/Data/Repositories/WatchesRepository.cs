using Microsoft.Data.Sqlite;
using SpankBank.Core.Models;

namespace SpankBank.Data.Repositories;

public sealed record WatchedArtistTag(long Id, string Tag, ArtistTagKind Kind, bool IsManual, bool Enabled);

/// <summary>Pool details from the imported catalog; null on a WatchEntry when the pool is not in the export yet.</summary>
public sealed record PoolInfo(string Name, string Category, int PostCount);

/// <summary>A pool that contains a watched artist's posts, waiting for a decision.</summary>
public sealed record PoolSuggestion(
    long ArtistWatchId, string Artist, long PoolId, string PoolName, string Category, int ArtistPosts, int PoolPosts, bool IsRoot)
{
    public double SharePercent => PoolPosts == 0 ? 0 : 100.0 * ArtistPosts / PoolPosts;

    /// <summary>Worth a look by default: a series the artist has a real part in, or a collection that is mostly theirs.</summary>
    public bool IsLikely => IsRoot && ArtistPosts >= 2
        && (Category == "series" ? SharePercent >= 30 : SharePercent >= 60);
}

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
    string? Error = null,
    string? Origin = null,
    int SuggestedPools = 0)
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

        var entries = new List<(long Id, string Source, string Display, bool Enabled, string? LocalName, int Count, bool Matched, string? Error, string? Origin)>();
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT Id, SourceValue, DisplayName, Enabled, LocalName, MatchCount, LastCheckedAt IS NOT NULL, ErrorMessage, Origin FROM WatchedItems WHERE ItemType = $type ORDER BY SourceValue COLLATE NOCASE;";
            cmd.Parameters.AddWithValue("$type", (int)type);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                entries.Add((reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3) != 0, reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.GetInt32(5), reader.GetInt32(6) != 0, reader.IsDBNull(7) ? null : reader.GetString(7),
                    reader.IsDBNull(8) ? null : reader.GetString(8)));
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
        var suggested = type == WatchedItemType.Artist ? SuggestedCountsByArtist() : [];

        return entries
            .Select(e =>
            {
                PoolInfo? pool = long.TryParse(e.Source, out var poolId) && pools.TryGetValue(poolId, out var info) ? info : null;
                var display = !string.IsNullOrWhiteSpace(e.LocalName) ? e.LocalName
                    : pool is not null ? pool.Name.Replace('_', ' ')
                    : e.Display;
                return new WatchEntry(e.Id, type, e.Source, display, e.Enabled,
                    tagsByItem.TryGetValue(e.Id, out var tags) ? tags : [], e.LocalName, pool, e.Count, e.Matched, e.Error,
                    e.Origin, suggested.GetValueOrDefault(e.Id));
            })
            .ToList();
    }

    /// <summary>The source values (artist tag, pool id) of every watch of a type, to mark what is already watched.</summary>
    public HashSet<string> WatchedSources(WatchedItemType type)
    {
        using var connection = new SqliteConnection(_appConnectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT SourceValue FROM WatchedItems WHERE ItemType = $type;";
        cmd.Parameters.AddWithValue("$type", (int)type);
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) set.Add(reader.GetString(0));
        return set;
    }

    // ---------------------------------------------------------------- pool suggestions

    /// <summary>Suggested pools waiting for a decision; by default only the likely ones.</summary>
    public IReadOnlyList<PoolSuggestion> GetPoolSuggestions(bool showAll, long? artistWatchId = null)
    {
        var rows = new List<(long Artist, string Name, long Pool, int ArtistPosts, int PoolPosts, bool Root)>();
        using (var connection = new SqliteConnection(_appConnectionString))
        {
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                SELECT s.ArtistWatchId, w.SourceValue, s.PoolId, s.ArtistPosts, s.PoolPosts, s.IsRoot
                FROM PoolSuggestions s
                JOIN WatchedItems w ON w.Id = s.ArtistWatchId
                WHERE s.State = 0 AND ($artist IS NULL OR s.ArtistWatchId = $artist);
                """;
            cmd.Parameters.AddWithValue("$artist", (object?)artistWatchId ?? DBNull.Value);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                rows.Add((reader.GetInt64(0), reader.GetString(1), reader.GetInt64(2), reader.GetInt32(3), reader.GetInt32(4), reader.GetInt32(5) != 0));
        }

        var pools = LoadPools(rows.Select(r => r.Pool.ToString()).Distinct());
        return rows
            .Where(r => pools.ContainsKey(r.Pool))
            .Select(r => new PoolSuggestion(r.Artist, r.Name, r.Pool, pools[r.Pool].Name.Replace('_', ' '), pools[r.Pool].Category, r.ArtistPosts, r.PoolPosts, r.Root))
            .Where(s => showAll || s.IsLikely)
            .OrderByDescending(s => s.SharePercent).ThenByDescending(s => s.ArtistPosts)
            .ToList();
    }

    /// <summary>Number of distinct likely pools to review (for the navigation badge).</summary>
    public int CountPoolSuggestions() => GetPoolSuggestions(showAll: false).Select(s => s.PoolId).Distinct().Count();

    private Dictionary<long, int> SuggestedCountsByArtist()
        => GetPoolSuggestions(showAll: false).GroupBy(s => s.ArtistWatchId).ToDictionary(g => g.Key, g => g.Count());

    /// <summary>Adds a suggested pool to the watches; every suggestion of that pool counts as handled.</summary>
    public void AcceptPoolSuggestion(long poolId, string artist)
    {
        Add(WatchedItemType.Pool, poolId.ToString(), $"Pool #{poolId}", origin: $"suggested:{artist}");
        SetSuggestionState("PoolId = $pool", poolId, 1);
    }

    /// <summary>Hides a suggestion for this artist for good.</summary>
    public void IgnorePoolSuggestion(long artistWatchId, long poolId)
    {
        using var connection = new SqliteConnection(_appConnectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE PoolSuggestions SET State = 2 WHERE ArtistWatchId = $artist AND PoolId = $pool AND State = 0;";
        cmd.Parameters.AddWithValue("$artist", artistWatchId);
        cmd.Parameters.AddWithValue("$pool", poolId);
        cmd.ExecuteNonQuery();
    }

    private void SetSuggestionState(string condition, long poolId, int state)
    {
        using var connection = new SqliteConnection(_appConnectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"UPDATE PoolSuggestions SET State = $state WHERE {condition} AND State = 0;";
        cmd.Parameters.AddWithValue("$state", state);
        cmd.Parameters.AddWithValue("$pool", poolId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Marks every enabled artist for re-matching, which also recomputes their pool suggestions.</summary>
    public void RecomputeArtistPools()
    {
        using var connection = new SqliteConnection(_appConnectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE WatchedItems SET LastCheckedAt = NULL WHERE ItemType = $artist;";
        cmd.Parameters.AddWithValue("$artist", (int)WatchedItemType.Artist);
        cmd.ExecuteNonQuery();
        RequestMatch();
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
    public long? Add(WatchedItemType type, string sourceValue, string displayName, string? origin = null)
    {
        using var connection = new SqliteConnection(_appConnectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO WatchedItems (ItemType, SourceValue, DisplayName, Enabled, AutoDownload, Origin)
            SELECT $type, $source, $display, 1, 1, $origin
            WHERE NOT EXISTS (SELECT 1 FROM WatchedItems WHERE ItemType = $type AND SourceValue = $source COLLATE NOCASE);
            """;
        cmd.Parameters.AddWithValue("$type", (int)type);
        cmd.Parameters.AddWithValue("$source", sourceValue);
        cmd.Parameters.AddWithValue("$display", displayName);
        cmd.Parameters.AddWithValue("$origin", (object?)origin ?? DBNull.Value);
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
