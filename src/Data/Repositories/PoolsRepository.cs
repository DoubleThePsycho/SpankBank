using Microsoft.Data.Sqlite;
using SpankBank.Core.Models;

namespace SpankBank.Data.Repositories;

public sealed class PoolsRepository
{
    private readonly string _connectionString;

    public PoolsRepository(string databasePath)
    {
        _connectionString = $"Data Source={databasePath}";
    }

    public PoolDetail? GetPoolById(long id)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT Id, Name, Category, PostIds, CreatedAt, UpdatedAt
            FROM Pools
            WHERE Id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", id);

        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return null;

        return new PoolDetail(
            Id: reader.GetInt64(0),
            Name: reader.GetString(1),
            DisplayName: reader.GetString(1),
            PoolType: ParsePoolType(reader.GetString(2)),
            PostCount: CountPostIds(reader.IsDBNull(3) ? string.Empty : reader.GetString(3)),
            CreatedAt: reader.IsDBNull(4) ? null : DateTimeOffset.Parse(reader.GetString(4)),
            UpdatedAt: reader.IsDBNull(5) ? null : DateTimeOffset.Parse(reader.GetString(5))
        );
    }

    public IReadOnlyList<PoolListItem> GetPools(
        int limit,
        PoolListFilter? filter = null,
        PoolOrderField orderBy = PoolOrderField.Name,
        SortDirection direction = SortDirection.Descending)
    {
        var directionSql = direction == SortDirection.Ascending ? "ASC" : "DESC";
        var orderClause = orderBy switch
        {
            PoolOrderField.Name      => $"Name COLLATE NOCASE {directionSql}, Id ASC",
            PoolOrderField.PostCount => $"PostCountComputed {directionSql}, Id ASC",
            PoolOrderField.CreatedAt => $"CreatedAt IS NULL ASC, CreatedAt {directionSql}, Id ASC",
            PoolOrderField.UpdatedAt => $"UpdatedAt IS NULL ASC, UpdatedAt {directionSql}, Id ASC",
            _                        => $"Id {directionSql}",
        };

        var (where, parameters) = BuildWhereClause(filter);

        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT Id, Name, Category,
                   CASE
                       WHEN TRIM(COALESCE(PostIds, '')) = '' THEN 0
                       ELSE LENGTH(TRIM(PostIds)) - LENGTH(REPLACE(TRIM(PostIds), ' ', '')) + 1
                   END AS PostCountComputed,
                   CreatedAt, UpdatedAt
            FROM Pools{where}
            ORDER BY {orderClause}
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$limit", limit);
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value);

        using var reader = cmd.ExecuteReader();
        var results = new List<PoolListItem>();
        while (reader.Read())
        {
            results.Add(new PoolListItem(
                Id: reader.GetInt64(0),
                Name: reader.GetString(1),
                DisplayName: reader.GetString(1),
                PoolType: ParsePoolType(reader.GetString(2)),
                PostCount: reader.GetInt32(3),
                CreatedAt: reader.IsDBNull(4) ? null : DateTimeOffset.Parse(reader.GetString(4)),
                UpdatedAt: reader.IsDBNull(5) ? null : DateTimeOffset.Parse(reader.GetString(5))
            ));
        }

        return results;
    }

    public long CountPools(PoolListFilter? filter = null)
    {
        var (where, parameters) = BuildWhereClause(filter);

        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM Pools{where};";
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value);

        return (long)(cmd.ExecuteScalar() ?? 0L);
    }

    public IReadOnlyList<PoolPostListItem> GetPostsForPool(long poolId, int limit = 500)
    {
        // Pool membership/order is stored in the pools export's post_ids column. Do not create PoolPosts.
        return [];
    }

    private static (string clause, List<(string name, object value)> parameters) BuildWhereClause(
        PoolListFilter? filter)
    {
        if (filter is null)
            return ("", []);

        var conditions = new List<string>();
        var parameters = new List<(string name, object value)>();

        if (filter.Id.HasValue)
        {
            conditions.Add("Id = $poolId");
            parameters.Add(("$poolId", filter.Id.Value));
        }

        if (!string.IsNullOrWhiteSpace(filter.Name))
        {
            conditions.Add("Name LIKE $poolName ESCAPE '\\'");
            parameters.Add(("$poolName", $"%{EscapeLike(filter.Name.Trim())}%"));
        }

        if (filter.PoolType.HasValue)
        {
            conditions.Add("Category = $poolType");
            parameters.Add(("$poolType", filter.PoolType.Value == PoolType.Series ? "series" : "collection"));
        }

        if (conditions.Count == 0)
            return ("", parameters);

        return ($" WHERE {string.Join(" AND ", conditions)}", parameters);
    }

    private static string EscapeLike(string value)
        => value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);

    private static PoolType ParsePoolType(string value)
        => value.Equals("series", StringComparison.OrdinalIgnoreCase)
            ? PoolType.Series
            : value.Equals("collection", StringComparison.OrdinalIgnoreCase)
                ? PoolType.Collection
                : PoolType.Unknown;

    private static int CountPostIds(string postIds)
    {
        postIds = postIds.Trim();
        if (string.IsNullOrWhiteSpace(postIds))
            return 0;

        return postIds.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
    }
}
