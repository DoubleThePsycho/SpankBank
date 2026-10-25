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
}
