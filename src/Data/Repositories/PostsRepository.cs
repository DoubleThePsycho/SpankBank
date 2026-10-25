using Microsoft.Data.Sqlite;
using SpankBank.Core.Models;

namespace SpankBank.Data.Repositories;

public sealed class PostsRepository
{
    private readonly string _connectionString;

    public PostsRepository(string databasePath)
    {
        _connectionString = $"Data Source={databasePath}";
    }

    public long CountPosts(PostListFilter? filter = null)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        var (where, parameters) = BuildWhereClause(filter);
        cmd.CommandText = $"SELECT COUNT(*) FROM Posts{where};";
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value);
        return (long)(cmd.ExecuteScalar() ?? 0L);
    }

    public IReadOnlyList<PostListItem> GetRecentPosts(
        int limit,
        PostOrderField orderBy = PostOrderField.DateUploaded,
        SortDirection direction = SortDirection.Descending,
        PostListFilter? filter = null,
        int offset = 0)
    {
        var directionSql = direction == SortDirection.Ascending ? "ASC" : "DESC";
        var orderClause = orderBy switch
        {
            PostOrderField.DateUploaded   => $"CreatedAt {directionSql}",
            PostOrderField.DateDownloaded => $"Id {directionSql}",
            PostOrderField.Score          => $"Score {directionSql}",
            PostOrderField.DateUpdated    => $"UpdatedAt {directionSql}",
            PostOrderField.FileSize       => $"FileSize IS NULL ASC, FileSize {directionSql}",
            _                             => $"CreatedAt {directionSql}",
        };

        var (where, parameters) = BuildWhereClause(filter);

        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT Id, Rating, CreatedAt, UpdatedAt, Score, FileExt, Md5, FavCount
            FROM Posts{where}
            ORDER BY {orderClause}
            LIMIT $limit OFFSET $offset;
            """;
        cmd.Parameters.AddWithValue("$limit", limit);
        cmd.Parameters.AddWithValue("$offset", offset);
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value);

        using var reader = cmd.ExecuteReader();
        var results = new List<PostListItem>();
        while (reader.Read())
        {
            results.Add(new PostListItem(
                Id: reader.GetInt64(0),
                Rating: (PostRating)reader.GetInt32(1),
                CreatedAt: DateTimeOffset.Parse(reader.GetString(2)),
                UpdatedAt: DateTimeOffset.Parse(reader.GetString(3)),
                Score: reader.GetInt32(4),
                FileExt: reader.IsDBNull(5) ? null : reader.GetString(5),
                Md5: reader.IsDBNull(6) ? null : reader.GetString(6),
                FavCount: reader.GetInt32(7)
            ));
        }
        return results;
    }

    public PostDetail? GetPostById(long id)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT Id, Rating, Score, CreatedAt, UpdatedAt, NULL AS FetchedAt, NULL AS SavedAt,
                   ImageWidth, ImageHeight, FileSize, FileExt, Md5, Source,
                   NULL AS FileLocalPath, ParentId, IsDeleted
            FROM Posts
            WHERE Id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", id);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;

        var detail = new PostDetail(
            Id:            reader.GetInt64(0),
            Rating:        (PostRating)reader.GetInt32(1),
            Score:         reader.GetInt32(2),
            CreatedAt:     DateTimeOffset.Parse(reader.GetString(3)),
            UpdatedAt:     DateTimeOffset.Parse(reader.GetString(4)),
            FetchedAt:     reader.IsDBNull(5)  ? null : DateTimeOffset.Parse(reader.GetString(5)),
            SavedAt:       reader.IsDBNull(6)  ? null : DateTimeOffset.Parse(reader.GetString(6)),
            FileWidth:     reader.IsDBNull(7)  ? null : reader.GetInt32(7),
            FileHeight:    reader.IsDBNull(8)  ? null : reader.GetInt32(8),
            FileSize:      reader.IsDBNull(9)  ? null : reader.GetInt64(9),
            FileExt:       reader.IsDBNull(10) ? null : reader.GetString(10),
            FileMd5:       reader.IsDBNull(11) ? null : reader.GetString(11),
            FileUrl:       reader.IsDBNull(12) ? null : reader.GetString(12),
            FileLocalPath: reader.IsDBNull(13) ? null : reader.GetString(13),
            ParentId:      reader.IsDBNull(14) ? null : reader.GetInt64(14),
            Deleted:       reader.GetInt32(15) != 0,
            Tags:          [],
            Pools:         [],
            ChildPostIds:  []
        );
        reader.Close();

        return detail with
        {
            Tags = LoadPostTags(connection, id),
            Pools = LoadPostPools(connection, id),
            ChildPostIds = LoadChildPostIds(connection, id)
        };
    }

    private static IReadOnlyList<PostDetailTag> LoadPostTags(SqliteConnection connection, long postId)
    {
        // Posts import will use the CSV tag_string column. Do not create a normalized PostTags table.
        return [];
    }

    private static IReadOnlyList<PostDetailPool> LoadPostPools(SqliteConnection connection, long postId)
    {
        // Pool membership/order comes from the pools CSV export's post_ids column. Do not create PoolPosts.
        return [];
    }

    private static IReadOnlyList<long> LoadChildPostIds(SqliteConnection connection, long postId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT Id
            FROM Posts
            WHERE ParentId = $postId
            ORDER BY Id;
            """;
        cmd.Parameters.AddWithValue("$postId", postId);

        using var reader = cmd.ExecuteReader();
        var results = new List<long>();
        while (reader.Read())
            results.Add(reader.GetInt64(0));
        return results;
    }

    private static (string clause, List<(string name, object value)> parameters) BuildWhereClause(
        PostListFilter? filter)
    {
        if (filter is null)
            return ("", []);

        var conditions = new List<string>();
        var parameters = new List<(string name, object value)>();

        // ID
        if (filter.Id is not null)
            AddNumericFilter("Id", filter.Id, "id", conditions, parameters);

        // Score
        if (filter.Score is not null)
            AddNumericFilter("Score", filter.Score, "sc", conditions, parameters);

        // Rating: collect which ratings are allowed
        var allowed = new List<int>(3);
        if (filter.IncludeSafe)         allowed.Add((int)PostRating.Safe);
        if (filter.IncludeQuestionable) allowed.Add((int)PostRating.Questionable);
        if (filter.IncludeExplicit)     allowed.Add((int)PostRating.Explicit);

        if (allowed.Count == 0)
        {
            conditions.Add("1 = 0");
        }
        else if (allowed.Count < 3)
        {
            var placeholders = allowed.Select((_, i) => $"$rating{i}");
            conditions.Add($"Rating IN ({string.Join(", ", placeholders)})");
            for (int i = 0; i < allowed.Count; i++)
                parameters.Add(($"$rating{i}", allowed[i]));
        }

        // File extensions: empty = any; non-empty = IN clause
        if (filter.FileExtensions.Count > 0)
        {
            var exts = filter.FileExtensions.ToList();
            var placeholders = exts.Select((_, i) => $"$fileExt{i}");
            conditions.Add($"FileExt IN ({string.Join(", ", placeholders)})");
            for (int i = 0; i < exts.Count; i++)
                parameters.Add(($"$fileExt{i}", exts[i]));
        }

        // Date uploaded (CreatedAt)
        if (filter.DateUploaded is not null)
            AddDateFilter("CreatedAt", filter.DateUploaded, "cup", conditions, parameters);

        // Date updated (UpdatedAt)
        if (filter.DateUpdated is not null)
            AddDateFilter("UpdatedAt", filter.DateUpdated, "uup", conditions, parameters);

        // Date downloaded is device/local state. Catalog-only server queries have no downloaded timestamp yet.
        if (filter.DateDownloaded is not null)
            conditions.Add("1 = 0");

        // Pool membership
        switch (filter.PoolMembership)
        {
            case PoolMembershipFilter.InPool:
                // TODO: Re-enable once pools import maps pools.post_ids into a queryable structure.
                conditions.Add("1 = 0");
                break;

            case PoolMembershipFilter.NotInPool:
                // Without imported pool membership data, catalog posts cannot be proven to be in a pool.
                break;
        }

        if (conditions.Count == 0)
            return ("", parameters);

        return ($" WHERE {string.Join(" AND ", conditions)}", parameters);
    }

    private static void AddNumericFilter(
        string column, NumericFilter filter, string prefix,
        List<string> conditions, List<(string, object)> parameters)
    {
        switch (filter.Kind)
        {
            case NumericFilterKind.Exact:
                conditions.Add($"{column} = ${prefix}val");
                parameters.Add(($"${prefix}val", filter.Value));
                break;
            case NumericFilterKind.GreaterThan:
                conditions.Add($"{column} > ${prefix}val");
                parameters.Add(($"${prefix}val", filter.Value));
                break;
            case NumericFilterKind.LessThan:
                conditions.Add($"{column} < ${prefix}val");
                parameters.Add(($"${prefix}val", filter.Value));
                break;
            case NumericFilterKind.GreaterOrEqual:
                conditions.Add($"{column} >= ${prefix}val");
                parameters.Add(($"${prefix}val", filter.Value));
                break;
            case NumericFilterKind.LessOrEqual:
                conditions.Add($"{column} <= ${prefix}val");
                parameters.Add(($"${prefix}val", filter.Value));
                break;
            case NumericFilterKind.Range:
                conditions.Add($"{column} >= ${prefix}lo");
                conditions.Add($"{column} <= ${prefix}hi");
                parameters.Add(($"${prefix}lo", filter.Value));
                parameters.Add(($"${prefix}hi", filter.High));
                break;
            case NumericFilterKind.List when filter.Items is { Count: > 0 }:
                var placeholders = filter.Items.Select((_, i) => $"${prefix}item{i}");
                conditions.Add($"{column} IN ({string.Join(", ", placeholders)})");
                for (int i = 0; i < filter.Items.Count; i++)
                    parameters.Add(($"${prefix}item{i}", filter.Items[i]));
                break;
        }
    }

    private static void AddDateFilter(
        string column, DateFilter filter, string prefix,
        List<string> conditions, List<(string, object)> parameters)
    {
        switch (filter.Kind)
        {
            case DateFilterKind.GreaterThan:
                conditions.Add($"{column} > ${prefix}val");
                parameters.Add(($"${prefix}val", filter.Value.ToUniversalTime().ToString("o")));
                break;
            case DateFilterKind.LessThan:
                conditions.Add($"{column} < ${prefix}val");
                parameters.Add(($"${prefix}val", filter.Value.ToUniversalTime().ToString("o")));
                break;
            case DateFilterKind.GreaterOrEqual:
                conditions.Add($"{column} >= ${prefix}val");
                parameters.Add(($"${prefix}val", filter.Value.ToUniversalTime().ToString("o")));
                break;
            case DateFilterKind.LessOrEqual:
                conditions.Add($"{column} <= ${prefix}val");
                parameters.Add(($"${prefix}val", filter.Value.ToUniversalTime().ToString("o")));
                break;
            case DateFilterKind.Range:
                conditions.Add($"{column} >= ${prefix}lo");
                conditions.Add($"{column} <= ${prefix}hi");
                parameters.Add(($"${prefix}lo", filter.Value.ToUniversalTime().ToString("o")));
                parameters.Add(($"${prefix}hi", filter.High.ToUniversalTime().ToString("o")));
                break;
        }
    }
}
