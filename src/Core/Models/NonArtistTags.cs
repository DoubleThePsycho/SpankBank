namespace SpankBank.Core.Models;

/// <summary>
/// Tags e621 files under the artist category that are markers, not people (huge post counts, nothing to follow).
/// They are never offered as artists, cannot be watched as artists and are left out of artist lists.
/// </summary>
public static class NonArtistTags
{
    public static readonly IReadOnlyList<string> Names =
    [
        "conditional_dnp", "avoid_posting", "unknown_artist", "unknown_artist_signature", "anonymous_artist",
        "third-party_edit", "sound_warning", "epilepsy_warning", "jumpscare_warning",
    ];

    private static readonly HashSet<string> Set = new(Names, StringComparer.OrdinalIgnoreCase);

    public static bool Contains(string name) => Set.Contains(name.Trim().Replace(' ', '_'));

    /// <summary>A SQL list of the names for a NOT IN (...) clause; the names are constants, never user input.</summary>
    public static string SqlList => string.Join(", ", Names.Select(n => $"'{n}'"));
}
