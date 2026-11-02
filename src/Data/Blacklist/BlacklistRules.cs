using System.Text.RegularExpressions;
using SpankBank.Core.Search;

namespace SpankBank.Data.Blacklist;

/// <summary>
/// The blacklist as rules: each line is an e621-style search (tags, -tag, ~tag, * wildcards, rating:, score:...).
/// A post is blacklisted when it matches any line. Deleted posts are included unless a line says otherwise.
/// </summary>
public sealed class BlacklistRules
{
    private sealed record Rule(PostSearchQuery Query, Func<HashSet<string>, string, bool>[] Required, Func<HashSet<string>, string, bool>[] Excluded, Func<HashSet<string>, string, bool>[][] AnyOfGroups);

    private readonly List<Rule> _rules;

    private BlacklistRules(List<Rule> rules) => _rules = rules;

    public int Count => _rules.Count;

    /// <summary>The error for a line that cannot be a blacklist entry (bad syntax, or terms only the posts page understands), else null.</summary>
    public static string? Validate(string line)
    {
        var query = ParseLine(line);
        if (query.Error is not null) return query.Error;
        if (query.NeedsDatabase) return "Pool, parent/child, favorite and similar terms cannot be used in the blacklist.";
        if (query.Required.Count + query.Excluded.Count + query.AnyOf.Count + query.Terms.Count == 0) return "The entry is empty.";
        return null;
    }

    public static BlacklistRules Create(IEnumerable<string> lines)
    {
        var rules = new List<Rule>();
        foreach (var line in lines)
        {
            if (Validate(line) is not null) continue;
            var query = ParseLine(line);
            rules.Add(new Rule(query,
                query.Required.Select(Matcher).ToArray(), query.Excluded.Select(Matcher).ToArray(), query.AnyOfGroups.Select(group => group.Select(Matcher).ToArray()).ToArray()));
        }
        return new BlacklistRules(rules);
    }

    /// <param name="tagString">The post's tags, space separated.</param>
    public bool Matches(string tagString, in PostMatchRow row)
    {
        if (_rules.Count == 0) return false;

        var tags = new HashSet<string>(tagString.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        foreach (var rule in _rules)
        {
            if (!rule.Query.MatchesRow(row)) continue;
            if (!rule.Required.All(m => m(tags, ""))) continue;
            if (rule.Excluded.Any(m => m(tags, ""))) continue;
            if (!rule.AnyOfGroups.All(group => group.Any(m => m(tags, "")))) continue;
            return true;
        }
        return false;
    }

    private static PostSearchQuery ParseLine(string line)
    {
        var text = line.Trim();
        var query = PostSearchQuery.Parse(text);
        // Match deleted posts too, unless the entry has its own status:.
        return query.StatusSpecified || query.Error is not null ? query : PostSearchQuery.Parse("status:any " + text);
    }

    private static Func<HashSet<string>, string, bool> Matcher(string term)
    {
        if (!term.Contains('*')) return (tags, _) => tags.Contains(term);
        var regex = new Regex("^" + Regex.Escape(term).Replace("\\*", ".*") + "$", RegexOptions.CultureInvariant);
        return (tags, _) => tags.Any(regex.IsMatch);
    }
}
