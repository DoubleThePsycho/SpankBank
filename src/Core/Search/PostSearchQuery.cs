using SpankBank.Core.Models;

namespace SpankBank.Core.Search;

/// <summary>The post columns a search can test, besides its tags.</summary>
public readonly record struct PostMatchRow(
    long Id,
    int Rating,
    int Score,
    int FavCount,
    string? FileExt,
    int Width,
    int Height,
    long? ParentId,
    long FileSize,
    bool IsDeleted,
    int CommentCount,
    string CreatedAt,
    string? Md5,
    int TagCount);

/// <summary>A metatag as written: name:value, possibly negated (-name:value).</summary>
public sealed record MetaTerm(string Name, string Value, bool Negated);

public enum PostStatusMode
{
    Active,
    Deleted,
    Any,
}

/// <summary>
/// An e621-style search, split into tag terms (resolved against the tag set by the caller) and column filters.
/// Supported: tags, -tag, ~tag, * wildcards, rating:, score:, favcount:, id:, width:, height:, filesize:, tagcount:,
/// comment_count:, date:, filetype:/type:, parent:, ischild:, status:, md5:. order: and limit: are recorded
/// (a watch ignores them; the posts page uses them). Anything else sets <see cref="Error"/>.
/// </summary>
public sealed class PostSearchQuery
{
    public IReadOnlyList<string> Required { get; private init; } = [];
    public IReadOnlyList<string> Excluded { get; private init; } = [];
    public IReadOnlyList<string> AnyOf { get; private init; } = [];
    public IReadOnlyList<Func<PostMatchRow, bool>> Filters { get; private init; } = [];
    public PostStatusMode Status { get; private init; } = PostStatusMode.Active;
    public bool StatusSpecified { get; private init; }
    /// <summary>The filtering metatags in order, for callers that build their own query (SQL).</summary>
    public IReadOnlyList<MetaTerm> Terms { get; private init; } = [];
    /// <summary>Value of order:, if given.</summary>
    public string? Order { get; private init; }
    /// <summary>Value of limit:, if given.</summary>
    public int? Limit { get; private init; }
    public string? Error { get; private init; }
    /// <summary>True when the search uses terms only the posts page can evaluate (pools, children), not a watch.</summary>
    public bool NeedsDatabase { get; private init; }

    /// <summary>Column-only part of the search (everything except tag terms).</summary>
    public bool MatchesRow(in PostMatchRow row)
    {
        switch (Status)
        {
            case PostStatusMode.Active when row.IsDeleted:
            case PostStatusMode.Deleted when !row.IsDeleted:
                return false;
        }

        foreach (var filter in Filters)
            if (!filter(row)) return false;
        return true;
    }

    public static PostSearchQuery Parse(string? text)
    {
        var required = new List<string>();
        var excluded = new List<string>();
        var anyOf = new List<string>();
        var filters = new List<Func<PostMatchRow, bool>>();
        var status = PostStatusMode.Active;
        var statusSpecified = false;
        var terms = new List<MetaTerm>();
        string? order = null;
        int? limit = null;
        string? error = null;
        var needsDatabase = false;

        foreach (var raw in (text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var token = raw.ToLowerInvariant();
            var negated = token.StartsWith('-');
            var optional = token.StartsWith('~');
            if (negated || optional) token = token[1..];
            if (token.Length == 0) continue;

            var colon = token.IndexOf(':');
            if (colon <= 0)
            {
                (negated ? excluded : optional ? anyOf : required).Add(token);
                continue;
            }

            var name = token[..colon];
            var value = token[(colon + 1)..];

            if (name == "order") { order = value; continue; }
            if (name == "limit") { if (int.TryParse(value, out var parsedLimit) && parsedLimit > 0) limit = parsedLimit; continue; }
            if (name == "randseed") continue;

            if (name == "status")
            {
                statusSpecified = true;
                status = value switch { "deleted" => PostStatusMode.Deleted, "any" => PostStatusMode.Any, "active" => PostStatusMode.Active, _ => status };
                if (value is not ("deleted" or "any" or "active")) error ??= $"Unsupported status:{value}";
                continue;
            }

            if (name is "inpool" or "pool" or "isparent" or "haschild" or "hasparent")
            {
                // hasparent is another name for ischild; the rest are answered from the database.
                if (name == "hasparent") name = "ischild";
                else
                {
                    needsDatabase = true;
                    terms.Add(new MetaTerm(name == "haschild" ? "isparent" : name, value, negated));
                    continue;
                }
            }

            var filter = BuildFilter(name, value);
            if (filter is null)
            {
                error ??= $"Unsupported or invalid search term: {raw}";
                continue;
            }

            filters.Add(negated ? row => !filter(row) : filter);
            terms.Add(new MetaTerm(name, value, negated));
        }

        return new PostSearchQuery
        {
            Required = required,
            Excluded = excluded,
            AnyOf = anyOf,
            Filters = filters,
            Status = status,
            StatusSpecified = statusSpecified,
            Terms = terms,
            Order = order,
            Limit = limit,
            NeedsDatabase = needsDatabase,
            Error = error,
        };
    }

    private static Func<PostMatchRow, bool>? BuildFilter(string name, string value)
    {
        switch (name)
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
                return rating < 0 ? null : row => row.Rating == rating;
            }
            case "score": return Numeric(value, row => row.Score);
            case "favcount": return Numeric(value, row => row.FavCount);
            case "id": return Numeric(value, row => row.Id);
            case "width": return Numeric(value, row => row.Width);
            case "height": return Numeric(value, row => row.Height);
            case "filesize": return Numeric(value, row => row.FileSize);
            case "tagcount": return Numeric(value, row => row.TagCount);
            case "comment_count": return Numeric(value, row => row.CommentCount);
            case "filetype" or "type":
            {
                var ext = value == "jpeg" ? "jpg" : value;
                return ext.Length == 0 ? null : row => string.Equals(row.FileExt, ext, StringComparison.OrdinalIgnoreCase);
            }
            case "md5": return value.Length == 0 ? null : row => string.Equals(row.Md5, value, StringComparison.OrdinalIgnoreCase);
            case "ischild" or "hasparent":
                return value switch
                {
                    "true" => row => row.ParentId is not null,
                    "false" => row => row.ParentId is null,
                    _ => null,
                };
            case "parent":
            {
                if (value == "none") return row => row.ParentId is null;
                if (value == "any") return row => row.ParentId is not null;
                var number = NumericSyntaxParser.Parse(value);
                return number is null ? null : row => row.ParentId is { } parent && Matches(number, parent);
            }
            case "date":
            {
                var date = DateSyntaxParser.Parse(value);
                return date is null ? null : DateMatcher(date);
            }
            default:
                return null;
        }
    }

    private static Func<PostMatchRow, bool>? Numeric(string value, Func<PostMatchRow, long> select)
    {
        var filter = NumericSyntaxParser.Parse(value);
        return filter is null ? null : row => Matches(filter, select(row));
    }

    private static Func<PostMatchRow, bool> DateMatcher(DateFilter filter)
    {
        // CreatedAt is stored as a UTC ISO-8601 string, so string comparison orders the same as time.
        string Iso(DateTimeOffset d) => d.ToUniversalTime().ToString("o");
        var value = Iso(filter.Value);
        var high = Iso(filter.High);
        return filter.Kind switch
        {
            DateFilterKind.GreaterThan => row => string.CompareOrdinal(row.CreatedAt, value) > 0,
            DateFilterKind.LessThan => row => string.CompareOrdinal(row.CreatedAt, value) < 0,
            DateFilterKind.GreaterOrEqual => row => string.CompareOrdinal(row.CreatedAt, value) >= 0,
            DateFilterKind.LessOrEqual => row => string.CompareOrdinal(row.CreatedAt, value) <= 0,
            DateFilterKind.Range => row => string.CompareOrdinal(row.CreatedAt, value) >= 0 && string.CompareOrdinal(row.CreatedAt, high) <= 0,
            _ => _ => false,
        };
    }

    private static bool Matches(NumericFilter filter, long value) => filter.Kind switch
    {
        NumericFilterKind.Exact => value == filter.Value,
        NumericFilterKind.GreaterThan => value > filter.Value,
        NumericFilterKind.LessThan => value < filter.Value,
        NumericFilterKind.GreaterOrEqual => value >= filter.Value,
        NumericFilterKind.LessOrEqual => value <= filter.Value,
        NumericFilterKind.Range => value >= filter.Value && value <= filter.High,
        NumericFilterKind.List => filter.Items?.Contains(value) ?? false,
        _ => false,
    };
}
