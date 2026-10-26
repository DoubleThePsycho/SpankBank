using System.Text;
using Microsoft.Data.Sqlite;

namespace SpankBank.Data.Repositories;

public sealed record TagRow(string Name, int Category, long PostCount);
public sealed record PoolRow(long Id, string Name, string Category, int PostCount, bool IsActive);
public sealed record ArtistRow(string Name, long PostCount, IReadOnlyList<string> OtherNames);
public sealed record PoolDetails(long Id, string Name, string Category, string? Description, bool IsActive,
    DateTimeOffset? CreatedAt, DateTimeOffset? UpdatedAt, IReadOnlyList<long> PostIds);
public sealed record Page<T>(IReadOnlyList<T> Rows, long Total);

/// <summary>Read-only browsing of the imported e621 catalog: tags, pools and artists (server.sqlite).</summary>
public sealed class CatalogBrowser
{
    // Tags in the artist category that are markers, not people.
    private static readonly string[] NotArtists =
    [
        "conditional_dnp", "unknown_artist", "anonymous_artist", "avoid_posting", "epilepsy_warning", "sound_warning", "third-party_edit",
    ];

    private readonly string _connectionString;

    public CatalogBrowser(string serverDatabasePath) => _connectionString = $"Data Source={serverDatabasePath};Mode=ReadOnly";

    // ---------------------------------------------------------------- tags

    /// <param name="order">"count" (default), "name" or "date" (newest first).</param>
    public Page<TagRow> SearchTags(string? nameMatches, int? category, string? order, bool hideEmpty, int page, int pageSize)
    {
        var (where, parameters) = TagWhere(nameMatches, category, hideEmpty, extra: null);
        var orderBy = order switch
        {
            "name" => "Name ASC",
            "date" => "Id DESC",
            _ => "PostCount DESC, Name ASC",
        };

        return Query(
            $"FROM Tags WHERE {where}", parameters, $"Name, Category, PostCount", orderBy, page, pageSize,
            reader => new TagRow(reader.GetString(0), reader.GetInt32(1), reader.GetInt64(2)));
    }

    // ---------------------------------------------------------------- artists

    public Page<ArtistRow> SearchArtists(string? nameMatches, string? order, bool hideEmpty, int page, int pageSize)
    {
        var exclusion = $"Name NOT IN ({string.Join(", ", NotArtists.Select(a => $"'{a}'"))})";
        var (where, parameters) = TagWhere(nameMatches, category: 1, hideEmpty, exclusion);
        var orderBy = order == "name" ? "Name ASC" : "PostCount DESC, Name ASC";

        var tags = Query(
            $"FROM Tags WHERE {where}", parameters, "Name, PostCount", orderBy, page, pageSize,
            reader => (Name: reader.GetString(0), Count: reader.GetInt64(1)));

        var aliases = AliasesOf(tags.Rows.Select(t => t.Name).ToList());
        return new Page<ArtistRow>(
            tags.Rows.Select(t => new ArtistRow(t.Name, t.Count, aliases.TryGetValue(t.Name, out var names) ? names : [])).ToList(),
            tags.Total);
    }

    /// <summary>The artist tag with this name, following an alias to the real tag. Null when there is no such artist.</summary>
    public ArtistRow? GetArtist(string name)
    {
        using var connection = Open();
        string canonical = name;
        using (var alias = connection.CreateCommand())
        {
            alias.CommandText = "SELECT ConsequentName FROM TagAliases WHERE AntecedentName = $name AND Status = 'active' LIMIT 1;";
            alias.Parameters.AddWithValue("$name", name);
            if (alias.ExecuteScalar() is string target) canonical = target;
        }

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT Name, PostCount FROM Tags WHERE Name = $name AND Category = 1;";
        cmd.Parameters.AddWithValue("$name", canonical);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;

        var found = reader.GetString(0);
        var count = reader.GetInt64(1);
        reader.Close();
        return new ArtistRow(found, count, AliasesOf([found]).GetValueOrDefault(found) ?? []);
    }

    private Dictionary<string, IReadOnlyList<string>> AliasesOf(IReadOnlyList<string> names)
    {
        var result = new Dictionary<string, IReadOnlyList<string>>();
        if (names.Count == 0) return result;

        using var connection = Open();
        using var cmd = connection.CreateCommand();
        var placeholders = names.Select((_, i) => $"$n{i}").ToList();
        cmd.CommandText = $"""
            SELECT ConsequentName, AntecedentName FROM TagAliases
            WHERE Status = 'active' AND ConsequentName IN ({string.Join(", ", placeholders)})
            ORDER BY AntecedentName COLLATE NOCASE;
            """;
        for (var i = 0; i < names.Count; i++) cmd.Parameters.AddWithValue(placeholders[i], names[i]);

        var grouped = new Dictionary<string, List<string>>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var target = reader.GetString(0);
            if (!grouped.TryGetValue(target, out var list)) grouped[target] = list = [];
            list.Add(reader.GetString(1));
        }
        foreach (var (target, list) in grouped) result[target] = list;
        return result;
    }

    // ---------------------------------------------------------------- pools

    /// <param name="order">"updated" (default), "name", "created" or "count".</param>
    public Page<PoolRow> SearchPools(string? name, string? description, string? category, bool? active, string? order, int page, int pageSize)
    {
        var parameters = new List<(string, object)>();
        var where = new StringBuilder("1 = 1");

        if (!string.IsNullOrWhiteSpace(name))
        {
            where.Append(" AND Name LIKE $name ESCAPE '\\'");
            parameters.Add(("$name", "%" + Like(name.Trim().Replace(' ', '_')).Replace("*", "%") + "%"));
        }
        if (!string.IsNullOrWhiteSpace(description))
        {
            where.Append(" AND Description LIKE $description ESCAPE '\\'");
            parameters.Add(("$description", "%" + Like(description.Trim()).Replace("*", "%") + "%"));
        }
        if (category is "series" or "collection")
        {
            where.Append(" AND Category = $category");
            parameters.Add(("$category", category));
        }
        if (active is { } isActive)
        {
            where.Append(" AND IsActive = $active");
            parameters.Add(("$active", isActive ? 1 : 0));
        }

        var orderBy = order switch
        {
            "name" => "Name COLLATE NOCASE ASC",
            "created" => "CreatedAt DESC",
            "count" => "PostCount DESC, Name ASC",
            _ => "UpdatedAt DESC",
        };

        return Query(
            $"FROM Pools WHERE {where}", parameters, "Id, Name, Category, PostCount, IsActive", orderBy, page, pageSize,
            reader => new PoolRow(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3), reader.GetInt32(4) != 0));
    }

    public PoolDetails? GetPool(long id)
    {
        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT Id, Name, Category, Description, IsActive, CreatedAt, UpdatedAt, PostIds FROM Pools WHERE Id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;

        return new PoolDetails(
            reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetInt32(4) != 0,
            reader.IsDBNull(5) ? null : DateTimeOffset.Parse(reader.GetString(5)),
            reader.IsDBNull(6) ? null : DateTimeOffset.Parse(reader.GetString(6)),
            reader.GetString(7).Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(i => long.TryParse(i, out var postId) ? postId : 0).Where(i => i > 0).ToList());
    }

    /// <summary>
    /// The md5 of each pool's cover: its first post that e621 has not deleted, looking at the first dozen. Pools without one are left out.
    /// </summary>
    public Dictionary<long, string> CoverMd5s(IReadOnlyCollection<long> poolIds)
    {
        var covers = new Dictionary<long, string>();
        if (poolIds.Count == 0) return covers;

        using var connection = Open();
        var firstIds = new Dictionary<long, List<long>>();
        foreach (var poolId in poolIds)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT PostIds FROM Pools WHERE Id = $id;";
            cmd.Parameters.AddWithValue("$id", poolId);
            if (cmd.ExecuteScalar() is string ids)
                firstIds[poolId] = ids.Split(',', StringSplitOptions.RemoveEmptyEntries).Take(12)
                    .Select(i => long.TryParse(i, out var id) ? id : 0).Where(i => i > 0).ToList();
        }

        var all = firstIds.Values.SelectMany(i => i).Distinct().ToList();
        if (all.Count == 0) return covers;

        var md5ById = new Dictionary<long, string>();
        using (var cmd = connection.CreateCommand())
        {
            var placeholders = all.Select((_, i) => $"$p{i}").ToList();
            cmd.CommandText = $"SELECT Id, Md5 FROM Posts WHERE IsDeleted = 0 AND Md5 IS NOT NULL AND Id IN ({string.Join(", ", placeholders)});";
            for (var i = 0; i < all.Count; i++) cmd.Parameters.AddWithValue(placeholders[i], all[i]);
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) md5ById[reader.GetInt64(0)] = reader.GetString(1);
        }

        foreach (var (poolId, ids) in firstIds)
            foreach (var id in ids)
                if (md5ById.TryGetValue(id, out var md5)) { covers[poolId] = md5; break; }
        return covers;
    }

    // ---------------------------------------------------------------- shared

    private static (string Where, List<(string, object)> Parameters) TagWhere(string? nameMatches, int? category, bool hideEmpty, string? extra)
    {
        var parameters = new List<(string, object)>();
        var where = new StringBuilder("1 = 1");

        var pattern = nameMatches?.Trim().ToLowerInvariant().Replace(' ', '_');
        if (!string.IsNullOrEmpty(pattern))
        {
            if (pattern.Contains('*'))
            {
                where.Append(" AND Name LIKE $name ESCAPE '\\'");
                parameters.Add(("$name", Like(pattern).Replace("*", "%")));
            }
            else
            {
                // A plain term is a prefix, which the name index can answer with a range scan.
                where.Append(" AND Name >= $lo AND Name < $hi");
                parameters.Add(("$lo", pattern));
                parameters.Add(("$hi", pattern + "￿"));
            }
        }
        if (category is { } c)
        {
            where.Append(" AND Category = $category");
            parameters.Add(("$category", c));
        }
        if (hideEmpty) where.Append(" AND PostCount > 0");
        if (extra is not null) where.Append(" AND ").Append(extra);
        return (where.ToString(), parameters);
    }

    private Page<T> Query<T>(string from, List<(string, object)> parameters, string columns, string orderBy, int page, int pageSize, Func<SqliteDataReader, T> read)
    {
        using var connection = Open();

        long total;
        using (var count = connection.CreateCommand())
        {
            count.CommandText = $"SELECT COUNT(*) {from};";
            foreach (var (name, value) in parameters) count.Parameters.AddWithValue(name, value);
            total = Convert.ToInt64(count.ExecuteScalar());
        }

        var rows = new List<T>();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT {columns} {from} ORDER BY {orderBy} LIMIT $limit OFFSET $offset;";
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
        cmd.Parameters.AddWithValue("$limit", pageSize);
        cmd.Parameters.AddWithValue("$offset", (long)Math.Max(0, page - 1) * pageSize);
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) rows.Add(read(reader));
        return new Page<T>(rows, total);
    }

    private static string Like(string value) => value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }
}
