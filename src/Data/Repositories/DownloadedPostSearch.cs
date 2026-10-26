using System.Text;
using Microsoft.Data.Sqlite;
using SpankBank.Core.Models;
using SpankBank.Core.Search;

namespace SpankBank.Data.Repositories;

public sealed record SearchPage(IReadOnlyList<PostListItem> Items, long Total, int PageSize, string? Error);

/// <summary>
/// e621-style search over the posts on this device. Tags and most columns come from the catalog row of each post
/// (fresh with every export import), joined to device.sqlite by id.
/// </summary>
public sealed class DownloadedPostSearch
{
    private const int MaxPageSize = 320;

    // Tags of the post, space-padded so " tag " matches whole tags only.
    private const string TagsExpr = "(' ' || COALESCE(s.TagString, d.TagString, '') || ' ')";
    private const string TagCountExpr = "(length(TRIM(COALESCE(s.TagString, d.TagString, ''))) - length(replace(TRIM(COALESCE(s.TagString, d.TagString, '')), ' ', '')) + 1)";

    private static readonly Dictionary<string, string> NumericColumns = new()
    {
        ["score"] = "d.Score",
        ["id"] = "d.Id",
        ["width"] = "d.ImageWidth",
        ["height"] = "d.ImageHeight",
        ["filesize"] = "d.FileSize",
        ["favcount"] = "COALESCE(s.FavCount, 0)",
        ["comment_count"] = "COALESCE(s.CommentCount, 0)",
        ["tagcount"] = TagCountExpr,
    };

    private static readonly Dictionary<string, string> OrderColumns = new()
    {
        ["id"] = "d.Id",
        ["score"] = "d.Score",
        ["favcount"] = "COALESCE(s.FavCount, 0)",
        ["filesize"] = "d.FileSize",
        ["mpixels"] = "(d.ImageWidth * d.ImageHeight)",
        ["tagcount"] = TagCountExpr,
        ["comment_count"] = "COALESCE(s.CommentCount, 0)",
        ["change"] = "d.UpdatedAt",
        ["favorited"] = "(SELECT f.CreatedAt FROM app.Favorites f WHERE f.PostId = d.Id)",
        ["downloaded"] = "d.SavedAt",
        ["saved"] = "d.SavedAt",
        ["landscape"] = "(d.ImageWidth * 1.0 / NULLIF(d.ImageHeight, 0))",
        ["portrait"] = "(d.ImageWidth * 1.0 / NULLIF(d.ImageHeight, 0))",
    };

    private readonly DatabasePaths _paths;

    public DownloadedPostSearch(DatabasePaths paths) => _paths = paths;

    public SearchPage Search(string? text, int page, int defaultPageSize)
    {
        var query = PostSearchQuery.Parse(text);
        var pageSize = Math.Clamp(query.Limit ?? defaultPageSize, 1, MaxPageSize);
        if (query.Error is not null)
            return new SearchPage([], 0, pageSize, query.Error);

        var parameters = new List<(string Name, object Value)>();
        var where = new StringBuilder("d.SavedAt IS NOT NULL AND NOT EXISTS (SELECT 1 FROM app.DeletedPosts dp WHERE dp.PostId = d.Id)");
        // Blacklisted posts stay hidden unless the search says blacklisted:true or blacklisted:any.
        if (!query.Terms.Any(t => t.Name == "blacklisted"))
            where.Append(" AND NOT EXISTS (SELECT 1 FROM app.BlockedPosts bl WHERE bl.PostId = d.Id)");
        string? error = null;

        string P(object value)
        {
            var name = $"$p{parameters.Count}";
            parameters.Add((name, value));
            return name;
        }

        foreach (var tag in query.Required) where.Append(" AND ").Append(TagCondition(tag, P));
        foreach (var tag in query.Excluded) where.Append(" AND NOT ").Append(TagCondition(tag, P));
        if (query.AnyOf.Count > 0)
            where.Append(" AND (").Append(string.Join(" OR ", query.AnyOf.Select(tag => TagCondition(tag, P)))).Append(')');

        foreach (var term in query.Terms)
        {
            var condition = MetaCondition(term, P);
            if (condition is null) { error = $"Unsupported search term: {term.Name}:{term.Value}"; break; }
            where.Append(" AND ").Append(term.Negated ? $"NOT ({condition})" : $"({condition})");
        }

        if (error is null && query.StatusSpecified)
        {
            switch (query.Status)
            {
                case PostStatusMode.Deleted: where.Append(" AND COALESCE(s.IsDeleted, 0) = 1"); break;
                case PostStatusMode.Active: where.Append(" AND COALESCE(s.IsDeleted, 0) = 0"); break;
            }
        }

        var orderBy = OrderClause(query.Order, ref error);
        if (error is not null) return new SearchPage([], 0, pageSize, error);

        using var connection = OpenWithCatalog();

        long total;
        using (var count = connection.CreateCommand())
        {
            count.CommandText = $"SELECT COUNT(*) FROM Posts d LEFT JOIN srv.Posts s ON s.Id = d.Id WHERE {where};";
            foreach (var (name, value) in parameters) count.Parameters.AddWithValue(name, value);
            total = Convert.ToInt64(count.ExecuteScalar());
        }

        var items = new List<PostListItem>();
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = $"""
                SELECT d.Id, d.Rating, d.CreatedAt, d.UpdatedAt, d.Score, d.FileExt, d.Md5, COALESCE(s.FavCount, 0)
                FROM Posts d LEFT JOIN srv.Posts s ON s.Id = d.Id
                WHERE {where}
                ORDER BY {orderBy}
                LIMIT $limit OFFSET $offset;
                """;
            foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
            cmd.Parameters.AddWithValue("$limit", pageSize);
            cmd.Parameters.AddWithValue("$offset", (long)Math.Max(0, page - 1) * pageSize);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                items.Add(new PostListItem(
                    Id: reader.GetInt64(0),
                    Rating: (PostRating)reader.GetInt32(1),
                    CreatedAt: DateTimeOffset.Parse(reader.GetString(2)),
                    UpdatedAt: DateTimeOffset.Parse(reader.GetString(3)),
                    Score: reader.GetInt32(4),
                    FileExt: reader.IsDBNull(5) ? null : reader.GetString(5),
                    Md5: reader.IsDBNull(6) ? null : reader.GetString(6),
                    FavCount: reader.GetInt32(7)));
            }
        }

        return new SearchPage(items, total, pageSize, null);
    }

    /// <summary>The given posts that are on this device, in no particular order.</summary>
    public IReadOnlyList<PostListItem> GetPosts(IReadOnlyCollection<long> ids)
    {
        var items = new List<PostListItem>();
        if (ids.Count == 0) return items;

        using var connection = OpenWithCatalog();
        using var cmd = connection.CreateCommand();
        var placeholders = ids.Select((_, i) => $"$i{i}").ToList();
        cmd.CommandText = $"""
            SELECT d.Id, d.Rating, d.CreatedAt, d.UpdatedAt, d.Score, d.FileExt, d.Md5, COALESCE(s.FavCount, 0)
            FROM Posts d LEFT JOIN srv.Posts s ON s.Id = d.Id
            WHERE d.SavedAt IS NOT NULL AND d.Id IN ({string.Join(", ", placeholders)});
            """;
        var index = 0;
        foreach (var id in ids) cmd.Parameters.AddWithValue(placeholders[index++], id);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            items.Add(new PostListItem(
                Id: reader.GetInt64(0),
                Rating: (PostRating)reader.GetInt32(1),
                CreatedAt: DateTimeOffset.Parse(reader.GetString(2)),
                UpdatedAt: DateTimeOffset.Parse(reader.GetString(3)),
                Score: reader.GetInt32(4),
                FileExt: reader.IsDBNull(5) ? null : reader.GetString(5),
                Md5: reader.IsDBNull(6) ? null : reader.GetString(6),
                FavCount: reader.GetInt32(7)));
        }
        return items;
    }

    /// <summary>
    /// The pools a post is in, each with the neighbouring posts that are on this device (pool order; posts that are
    /// only online are skipped, because they have no page here). The selected pool comes first, then by name; at most five.
    /// </summary>
    public IReadOnlyList<PoolNavItem> GetPoolNavigation(long postId, long? selectedPoolId)
    {
        using var connection = OpenWithCatalog();

        var pools = new List<(long Id, string Name, string Category, int Count, int Position)>();
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT pl.Id, pl.Name, pl.Category, pl.PostCount, pp.Position
                FROM srv.PoolPosts pp JOIN srv.Pools pl ON pl.Id = pp.PoolId
                WHERE pp.PostId = $post;
                """;
            cmd.Parameters.AddWithValue("$post", postId);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                pools.Add((reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3), reader.GetInt32(4)));
        }

        long? Neighbour(long poolId, int position, string comparison, string direction, out int foundPosition)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = $"""
                SELECT pp.PostId, pp.Position
                FROM srv.PoolPosts pp JOIN Posts d ON d.Id = pp.PostId AND d.SavedAt IS NOT NULL
                WHERE pp.PoolId = $pool AND pp.Position {comparison} $position
                ORDER BY pp.Position {direction} LIMIT 1;
                """;
            cmd.Parameters.AddWithValue("$pool", poolId);
            cmd.Parameters.AddWithValue("$position", position);
            using var reader = cmd.ExecuteReader();
            if (reader.Read()) { foundPosition = reader.GetInt32(1); return reader.GetInt64(0); }
            foundPosition = 0;
            return null;
        }

        return pools
            .OrderBy(p => p.Id == selectedPoolId ? 0 : 1)
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Take(5)
            .Select(p =>
            {
                var first = Neighbour(p.Id, p.Position, "<", "ASC", out var firstPage);
                var previous = Neighbour(p.Id, p.Position, "<", "DESC", out var previousPage);
                var next = Neighbour(p.Id, p.Position, ">", "ASC", out var nextPage);
                var last = Neighbour(p.Id, p.Position, ">", "DESC", out var lastPage);
                return new PoolNavItem(p.Id, p.Name.Replace('_', ' '), p.Category, p.Count, p.Position, p.Id == selectedPoolId,
                    first, firstPage, previous, previousPage, next, nextPage, last, lastPage);
            })
            .ToList();
    }

    /// <summary>How many of the given posts carry each tag, for the sidebar tag list.</summary>
    public IReadOnlyDictionary<string, int> TagFrequencies(IReadOnlyCollection<long> postIds)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        if (postIds.Count == 0) return counts;

        using var connection = OpenWithCatalog();
        using var cmd = connection.CreateCommand();
        var placeholders = postIds.Select((_, i) => $"$i{i}").ToList();
        cmd.CommandText = $"""
            SELECT COALESCE(s.TagString, d.TagString, '')
            FROM Posts d LEFT JOIN srv.Posts s ON s.Id = d.Id
            WHERE d.Id IN ({string.Join(", ", placeholders)});
            """;
        var index = 0;
        foreach (var id in postIds) cmd.Parameters.AddWithValue(placeholders[index++], id);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            foreach (var tag in reader.GetString(0).Split(' ', StringSplitOptions.RemoveEmptyEntries))
                counts[tag] = counts.GetValueOrDefault(tag) + 1;
        return counts;
    }

    // ---------------------------------------------------------------- SQL pieces

    private static string TagCondition(string tag, Func<object, string> p)
    {
        if (!tag.Contains('*'))
            return $"instr({TagsExpr}, {p(" " + tag + " ")}) > 0";

        var like = tag.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_").Replace("*", "%");
        return $"{TagsExpr} LIKE {p("% " + like + " %")} ESCAPE '\\'";
    }

    private static string? MetaCondition(MetaTerm term, Func<object, string> p)
    {
        var value = term.Value;
        switch (term.Name)
        {
            case "rating":
            {
                var rating = value switch
                {
                    "s" or "safe" => (int)PostRating.Safe,
                    "q" or "questionable" => (int)PostRating.Questionable,
                    "e" or "explicit" => (int)PostRating.Explicit,
                    _ => -1,
                };
                return rating < 0 ? null : $"d.Rating = {p(rating)}";
            }
            case "filetype" or "type":
                return value.Length == 0 ? null : $"d.FileExt = {p(value == "jpeg" ? "jpg" : value)} COLLATE NOCASE";
            case "md5":
                return value.Length == 0 ? null : $"d.Md5 = {p(value)} COLLATE NOCASE";
            case "parent":
            {
                if (value == "none") return "d.ParentId IS NULL";
                if (value == "any") return "d.ParentId IS NOT NULL";
                var filter = Core.Search.NumericSyntaxParser.Parse(value);
                return filter is null ? null : NumericCondition("d.ParentId", filter, p);
            }
            case "ischild" or "hasparent":
                return value switch { "true" => "d.ParentId IS NOT NULL", "false" => "d.ParentId IS NULL", _ => null };
            case "isparent":
                // Has a child among the local posts.
                return value switch
                {
                    "true" => "EXISTS (SELECT 1 FROM Posts c WHERE c.ParentId = d.Id AND c.SavedAt IS NOT NULL)",
                    "false" => "NOT EXISTS (SELECT 1 FROM Posts c WHERE c.ParentId = d.Id AND c.SavedAt IS NOT NULL)",
                    _ => null,
                };
            case "fav":
                // Favorites are the user's own (SpankBank.sqlite, attached as "app"); there is only one user, so any value but a negative one means "mine".
                return value switch
                {
                    "true" or "me" or "any" => "EXISTS (SELECT 1 FROM app.Favorites f WHERE f.PostId = d.Id)",
                    "false" or "none" => "NOT EXISTS (SELECT 1 FROM app.Favorites f WHERE f.PostId = d.Id)",
                    _ => null,
                };
            case "blacklisted":
                return value switch
                {
                    "true" => "EXISTS (SELECT 1 FROM app.BlockedPosts bl WHERE bl.PostId = d.Id)",
                    "false" => "NOT EXISTS (SELECT 1 FROM app.BlockedPosts bl WHERE bl.PostId = d.Id)",
                    "any" => "1 = 1",
                    _ => null,
                };
            case "folder":
                // A favorites folder: "none" is the unfiled favorites, "any" every filed one, a number that folder only.
                if (value == "none") return "NOT EXISTS (SELECT 1 FROM app.FavoriteFolderPosts fp WHERE fp.PostId = d.Id)";
                if (value == "any") return "EXISTS (SELECT 1 FROM app.FavoriteFolderPosts fp WHERE fp.PostId = d.Id)";
                return long.TryParse(value, out var folderId)
                    ? $"EXISTS (SELECT 1 FROM app.FavoriteFolderPosts fp WHERE fp.PostId = d.Id AND fp.FolderId = {p(folderId)})"
                    : null;
            case "inpool":
                return value switch
                {
                    "true" => "EXISTS (SELECT 1 FROM srv.PoolPosts pp WHERE pp.PostId = d.Id)",
                    "false" => "NOT EXISTS (SELECT 1 FROM srv.PoolPosts pp WHERE pp.PostId = d.Id)",
                    _ => null,
                };
            case "pool":
            {
                if (value == "any") return "EXISTS (SELECT 1 FROM srv.PoolPosts pp WHERE pp.PostId = d.Id)";
                if (value == "none") return "NOT EXISTS (SELECT 1 FROM srv.PoolPosts pp WHERE pp.PostId = d.Id)";
                return long.TryParse(value, out var poolId)
                    ? $"EXISTS (SELECT 1 FROM srv.PoolPosts pp WHERE pp.PostId = d.Id AND pp.PoolId = {p(poolId)})"
                    : null;
            }
            case "date":
            {
                var filter = DateSyntaxParser.Parse(value);
                return filter is null ? null : DateCondition("d.CreatedAt", filter, p);
            }
            default:
                if (NumericColumns.TryGetValue(term.Name, out var column))
                {
                    var filter = NumericSyntaxParser.Parse(value);
                    return filter is null ? null : NumericCondition(column, filter, p);
                }
                return null;
        }
    }

    private static string NumericCondition(string column, NumericFilter filter, Func<object, string> p) => filter.Kind switch
    {
        NumericFilterKind.Exact => $"{column} = {p(filter.Value)}",
        NumericFilterKind.GreaterThan => $"{column} > {p(filter.Value)}",
        NumericFilterKind.LessThan => $"{column} < {p(filter.Value)}",
        NumericFilterKind.GreaterOrEqual => $"{column} >= {p(filter.Value)}",
        NumericFilterKind.LessOrEqual => $"{column} <= {p(filter.Value)}",
        NumericFilterKind.Range => $"{column} BETWEEN {p(filter.Value)} AND {p(filter.High)}",
        NumericFilterKind.List => $"{column} IN ({string.Join(", ", (filter.Items ?? []).Select(i => p(i)))})",
        _ => "0",
    };

    private static string DateCondition(string column, DateFilter filter, Func<object, string> p)
    {
        string Iso(DateTimeOffset d) => d.ToUniversalTime().ToString("o");
        return filter.Kind switch
        {
            DateFilterKind.GreaterThan => $"{column} > {p(Iso(filter.Value))}",
            DateFilterKind.LessThan => $"{column} < {p(Iso(filter.Value))}",
            DateFilterKind.GreaterOrEqual => $"{column} >= {p(Iso(filter.Value))}",
            DateFilterKind.LessOrEqual => $"{column} <= {p(Iso(filter.Value))}",
            DateFilterKind.Range => $"{column} BETWEEN {p(Iso(filter.Value))} AND {p(Iso(filter.High))}",
            _ => "0",
        };
    }

    private static string OrderClause(string? order, ref string? error)
    {
        const string tieBreak = ", d.Id DESC";
        // Newest id first, like e621. Id is the table's primary key, so this walks the key order; the order
        // the rows were inserted in does not matter.
        if (string.IsNullOrEmpty(order)) return "d.Id DESC";
        if (order == "random") return "RANDOM()";

        var ascending = false;
        var name = order;
        if (name.EndsWith("_asc")) { ascending = true; name = name[..^4]; }
        else if (name.EndsWith("_desc")) name = name[..^5];
        if (name == "portrait") ascending = !ascending; // portrait is narrowest first

        if (!OrderColumns.TryGetValue(name, out var column))
        {
            error = $"Unsupported order: {order}";
            return "d.Id";
        }

        return $"{column} {(ascending ? "ASC" : "DESC")}{tieBreak}";
    }

    // The device database with the catalog attached as "srv" and the app state as "app". Not pooled: a pooled connection would keep the attachment.
    private SqliteConnection OpenWithCatalog()
    {
        var connection = new SqliteConnection($"Data Source={_paths.DeviceDatabasePath};Pooling=False");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"ATTACH DATABASE '{_paths.ServerDatabasePath.Replace("'", "''")}' AS srv; ATTACH DATABASE '{_paths.AppDatabasePath.Replace("'", "''")}' AS app;";
        cmd.ExecuteNonQuery();
        return connection;
    }
}

/// <summary>One pool's navigation bar on a post page. The ids are the nearest posts on this device, or null at the ends.</summary>
public sealed record PoolNavItem(
    long PoolId, string Name, string Category, int PostCount, int Page, bool Selected,
    long? FirstId, int FirstPage, long? PreviousId, int PreviousPage, long? NextId, int NextPage, long? LastId, int LastPage);
