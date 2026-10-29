using SpankBank.Core.Search;
using SpankBank.Data.Blacklist;

namespace SpankBank.Data.Matching;

/// <summary>
/// An artist's own whitelist and blacklist. Each line is a tag expression written like a post search
/// (<c>rating:s canine</c> is one expression). A post counts as the artist's when it matches at least one whitelist
/// line (if there are any) and no blacklist line.
/// </summary>
public sealed class ArtistFilter
{
    private readonly BlacklistRules _whitelist;
    private readonly BlacklistRules _blacklist;

    private ArtistFilter(BlacklistRules whitelist, BlacklistRules blacklist)
    {
        _whitelist = whitelist;
        _blacklist = blacklist;
    }

    public bool IsEmpty => _whitelist.Count == 0 && _blacklist.Count == 0;

    public static ArtistFilter Create(string? whitelist, string? blacklist)
        => new(BlacklistRules.Create(Lines(whitelist)), BlacklistRules.Create(Lines(blacklist)));

    public static IEnumerable<string> Lines(string? text)
        => (text ?? "").Split('\n').Select(l => string.Join(' ', l.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))).Where(l => l.Length > 0);

    public bool Allows(string tagString, in PostMatchRow row)
    {
        if (_whitelist.Count > 0 && !_whitelist.Matches(tagString, row)) return false;
        return _blacklist.Count == 0 || !_blacklist.Matches(tagString, row);
    }
}
