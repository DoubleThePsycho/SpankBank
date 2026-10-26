using SpankBank.Core.Models;

namespace SpankBank.Data.Repositories;

public sealed record UnrecognizedArtist(string Input, IReadOnlyList<string> Suggestions);

public sealed record BulkAddResult(
    IReadOnlyList<string> Added,
    IReadOnlyList<string> AlreadyWatched,
    IReadOnlyList<UnrecognizedArtist> Unrecognized);

/// <summary>Adds many artist watches from pasted text, with the same rules as adding one.</summary>
public static class ArtistBulkAdd
{
    /// <summary>Names separated by new lines, commas or semicolons; a leading "artist:" is ignored; duplicates collapse.</summary>
    public static IReadOnlyList<string> ParseNames(string? text)
    {
        var names = new List<string>();
        var seen = new HashSet<string>();
        foreach (var raw in (text ?? "").Split(['\n', '\r', ',', ';'], StringSplitOptions.RemoveEmptyEntries))
        {
            var name = raw.Trim();
            if (name.StartsWith("artist:", StringComparison.OrdinalIgnoreCase)) name = name["artist:".Length..].Trim();
            if (name.Length == 0) continue;
            if (seen.Add(WatchesRepository.Normalize(name))) names.Add(name);
        }
        return names;
    }

    public static BulkAddResult Run(WatchesRepository watches, string? text)
    {
        var added = new List<string>();
        var already = new List<string>();
        var unrecognized = new List<UnrecognizedArtist>();
        var handled = new HashSet<string>();

        foreach (var input in ParseNames(text))
        {
            var tag = watches.ResolveArtistTag(input);
            if (!watches.IsArtistTag(tag))
            {
                unrecognized.Add(new UnrecognizedArtist(input, watches.SuggestArtistTags(WatchesRepository.Normalize(input))));
                continue;
            }

            if (!handled.Add(tag)) continue; // two names that resolve to the same artist
            var id = watches.Add(WatchedItemType.Artist, tag, tag);
            if (id is null)
            {
                already.Add(tag);
                continue;
            }

            watches.RefreshArtist(id.Value, tag);
            added.Add(tag);
        }

        return new BulkAddResult(added, already, unrecognized);
    }
}
