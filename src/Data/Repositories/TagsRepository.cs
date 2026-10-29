using Microsoft.Data.Sqlite;
using SpankBank.Core.Models;

namespace SpankBank.Data.Repositories;

public sealed class TagsRepository
{
    private readonly string _connectionString;

    public TagsRepository(string databasePath)
    {
        _connectionString = $"Data Source={databasePath}";
    }

    public IReadOnlyList<TagListItem> GetTags(
        int limit,
        int offset = 0,
        TagListFilter? filter = null,
        TagOrderField orderBy = TagOrderField.PostCount,
        SortDirection direction = SortDirection.Descending)
    {
        // TODO: Deep OFFSET pages can become slow on very large tag/post tables.
        // Keep unrestricted page access for now, then move hot paths to keyset pagination or cached page anchors.
        var directionSql = direction == SortDirection.Ascending ? "ASC" : "DESC";
        var orderClause = orderBy switch
        {
            TagOrderField.Name => $"t.Name COLLATE NOCASE {directionSql}",
            _ => $"t.PostCount {directionSql}, t.Name COLLATE NOCASE ASC",
        };

        var (where, parameters) = BuildWhereClause(filter);

        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT t.Id,
                   t.Name,
                   COALESCE((
                       SELECT group_concat(ta.AntecedentName, ', ')
                       FROM (
                           SELECT AntecedentName
                           FROM TagAliases
                           WHERE ConsequentName = t.Name
                             AND Status = 'active'
                           ORDER BY AntecedentName COLLATE NOCASE
                       ) ta
                   ), '') AS Aliases,
                   t.Category,
                   t.PostCount
            FROM Tags t
            {where}
            ORDER BY {orderClause}
            LIMIT $limit OFFSET $offset;
            """;
        cmd.Parameters.AddWithValue("$limit", limit);
        cmd.Parameters.AddWithValue("$offset", Math.Max(0, offset));
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value);

        using var reader = cmd.ExecuteReader();
        var results = new List<TagListItem>();
        while (reader.Read())
        {
            results.Add(new TagListItem(
                Id: reader.GetInt64(0),
                Name: reader.GetString(1),
                Aliases: reader.GetString(2),
                Category: (TagCategory)reader.GetInt32(3),
                PostCount: reader.GetInt32(4)
            ));
        }

        return results;
    }

    public long CountTags(TagListFilter? filter = null)
    {
        var (where, parameters) = BuildWhereClause(filter);

        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM Tags t{where};";
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value);

        return (long)(cmd.ExecuteScalar() ?? 0L);
    }

    private static (string clause, List<(string name, object value)> parameters) BuildWhereClause(
        TagListFilter? filter)
    {
        var conditions = new List<string> { "t.PostCount > 0" };
        var parameters = new List<(string name, object value)>();

        if (!string.IsNullOrWhiteSpace(filter?.Name))
        {
            conditions.Add("t.Name LIKE $tagName ESCAPE '\\'");
            parameters.Add(("$tagName", $"%{EscapeLike(filter.Name.Trim())}%"));
        }

        if (filter?.Category.HasValue == true)
        {
            conditions.Add("t.Category = $category");
            parameters.Add(("$category", (int)filter.Category.Value));
        }

        return ($" WHERE {string.Join(" AND ", conditions)}", parameters);
    }

    private static string EscapeLike(string value)
        => value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);

    /// <summary>Category and export post count for each of the given tag names (names not in the catalog are left out).</summary>
    public IReadOnlyList<TagInfo> GetTagInfo(IReadOnlyCollection<string> names)
    {
        if (names.Count == 0) return [];

        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        var placeholders = names.Select((_, i) => $"$n{i}").ToList();
        cmd.CommandText = $"SELECT Name, Category, PostCount, Id FROM Tags WHERE Name IN ({string.Join(", ", placeholders)});";
        var index = 0;
        foreach (var name in names) cmd.Parameters.AddWithValue(placeholders[index++], name);

        using var reader = cmd.ExecuteReader();
        var result = new List<TagInfo>();
        while (reader.Read()) result.Add(new TagInfo(reader.GetString(0), reader.GetInt32(1), reader.GetInt64(2), reader.GetInt64(3)));
        return result;
    }

    /// <summary>
    /// e621ng-style tag autocomplete: names (or active aliases) matching the term, most used first.
    /// Counts are the export's post counts, not local ones. "*" is a wildcard; without one the term is a prefix.
    /// </summary>
    public IReadOnlyList<TagSuggestion> Autocomplete(string term, int? category = null, int limit = 20)
    {
        var pattern = term.Trim().ToLowerInvariant().Replace(' ', '_');
        if (pattern.Length == 0) return [];

        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var results = new List<TagSuggestion>();
        var categoryClause = category is null ? "" : " AND t.Category = $category";
        // The artist box never offers marker tags such as unknown_artist or conditional_dnp.
        if (category == 1) categoryClause += $" AND t.Name NOT IN ({NonArtistTags.SqlList})";

        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = $"""
                SELECT t.Id, t.Name, t.Category, t.PostCount, NULL
                FROM Tags t
                WHERE {NameCondition("t.Name", pattern, cmd)} AND t.PostCount > 0{categoryClause}
                ORDER BY t.PostCount DESC
                LIMIT $limit;
                """;
            AddCommon(cmd, category, limit);
            ReadSuggestions(cmd, results);
        }

        if (results.Count < limit)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = $"""
                SELECT t.Id, t.Name, t.Category, t.PostCount, a.AntecedentName
                FROM TagAliases a
                JOIN Tags t ON t.Name = a.ConsequentName
                WHERE {NameCondition("a.AntecedentName", pattern, cmd)} AND a.Status = 'active' AND t.PostCount > 0{categoryClause}
                ORDER BY t.PostCount DESC
                LIMIT $limit;
                """;
            AddCommon(cmd, category, limit);
            var aliasMatches = new List<TagSuggestion>();
            ReadSuggestions(cmd, aliasMatches);
            foreach (var match in aliasMatches)
                if (results.All(r => r.Id != match.Id) && results.Count < limit)
                    results.Add(match);
        }

        return results.OrderByDescending(r => r.PostCount).ToList();
    }

    // Plain prefixes use a range scan on the name index; wildcards fall back to LIKE.
    private static string NameCondition(string column, string pattern, SqliteCommand cmd)
    {
        if (!pattern.Contains('*'))
        {
            cmd.Parameters.AddWithValue("$lo", pattern);
            cmd.Parameters.AddWithValue("$hi", pattern + "\uffff");
            return $"{column} >= $lo AND {column} < $hi";
        }

        var like = pattern.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_").Replace("*", "%");
        cmd.Parameters.AddWithValue("$like", like);
        return $"{column} LIKE $like ESCAPE '\\'";
    }

    private static void AddCommon(SqliteCommand cmd, int? category, int limit)
    {
        cmd.Parameters.AddWithValue("$limit", limit);
        if (category is not null) cmd.Parameters.AddWithValue("$category", category.Value);
    }

    private static void ReadSuggestions(SqliteCommand cmd, List<TagSuggestion> into)
    {
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            into.Add(new TagSuggestion(reader.GetInt64(0), reader.GetString(1), reader.GetInt32(2), reader.GetInt64(3),
                reader.IsDBNull(4) ? null : reader.GetString(4)));
    }
}

public sealed record TagInfo(string Name, int Category, long PostCount, long Id = 0);

public sealed record TagSuggestion(long Id, string Name, int Category, long PostCount, string? AntecedentName);
