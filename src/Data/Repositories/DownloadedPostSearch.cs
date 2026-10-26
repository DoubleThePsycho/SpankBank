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
        var where = new StringBuilder("d.SavedAt IS NOT NULL");
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
        if (string.IsNullOrEmpty(order)) return "d.SavedAt DESC" + tieBreak;
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

    // The device database with the catalog attached as "srv". Not pooled: a pooled connection would keep the attachment.
    private SqliteConnection OpenWithCatalog()
    {
        var connection = new SqliteConnection($"Data Source={_paths.DeviceDatabasePath};Pooling=False");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"ATTACH DATABASE '{_paths.ServerDatabasePath.Replace("'", "''")}' AS srv;";
        cmd.ExecuteNonQuery();
        return connection;
    }
}
