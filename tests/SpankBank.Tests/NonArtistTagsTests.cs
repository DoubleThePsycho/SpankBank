using SpankBank.Core.Models;
using SpankBank.Data.Repositories;

namespace SpankBank.Tests;

public class NonArtistTagsTests : IDisposable
{
    private readonly TempDatabases _db = new();
    private readonly WatchesRepository _watches;

    public NonArtistTagsTests()
    {
        _watches = new WatchesRepository(_db.Paths.AppDatabasePath, _db.Paths.ServerDatabasePath);
        _db.Execute(_db.Paths.ServerDatabasePath, """
            INSERT INTO Tags (Id, Name, Category, PostCount) VALUES
                (1, 'unknown_artist', 1, 58000), (2, 'sound_warning', 1, 22000), (3, 'third-party_edit', 1, 32000),
                (4, 'anonymous_artist', 1, 11000), (5, 'conditional_dnp', 1, 160000), (6, 'unknown_colorist', 0, 70),
                (7, 'unknownartist_real', 1, 40), (8, 'loimu', 1, 500);
            INSERT INTO TagAliases (Id, AntecedentName, ConsequentName, Status) VALUES (1, 'anon', 'anonymous_artist', 'active');
            """);
    }

    public void Dispose() => _db.Dispose();

    [Theory]
    [InlineData("unknown_artist")]
    [InlineData("Sound Warning")]
    [InlineData("third-party_edit")]
    [InlineData("ANONYMOUS_ARTIST")]
    [InlineData("conditional_dnp")]
    public void Marker_tags_are_not_artists(string name)
    {
        Assert.True(NonArtistTags.Contains(name));
        Assert.False(_watches.IsArtistTag(WatchesRepository.Normalize(name)));
    }

    [Fact] public void Real_artists_are_unaffected() { Assert.False(NonArtistTags.Contains("loimu")); Assert.True(_watches.IsArtistTag("loimu")); Assert.True(_watches.IsArtistTag("unknownartist_real")); }

    [Fact]
    public void The_artist_autocomplete_leaves_markers_out_even_through_an_alias()
    {
        var tags = new TagsRepository(_db.Paths.ServerDatabasePath);
        Assert.Equal(["unknownartist_real"], tags.Autocomplete("unknown", category: 1).Select(t => t.Name));
        Assert.Empty(tags.Autocomplete("anon", category: 1));
        Assert.Contains(tags.Autocomplete("unknown", category: null), t => t.Name == "unknown_artist"); // the general tag search still finds it
    }

    [Fact]
    public void Bulk_add_lists_markers_as_unrecognized_and_never_suggests_them()
    {
        var result = ArtistBulkAdd.Run(_watches, "unknown_artist\nloimu\nunknown_art");

        Assert.Equal(["loimu"], result.Added);
        Assert.Equal(["unknown_artist", "unknown_art"], result.Unrecognized.Select(u => u.Input));
        Assert.DoesNotContain("unknown_artist", result.Unrecognized.SelectMany(u => u.Suggestions));
    }

    [Fact]
    public void Existing_watches_on_markers_are_removed_with_their_matches()
    {
        var bad = _watches.Add(WatchedItemType.Artist, "unknown_artist", "unknown_artist")!.Value;
        var good = _watches.Add(WatchedItemType.Artist, "loimu", "loimu")!.Value;
        _db.Execute(_db.Paths.AppDatabasePath, $"INSERT INTO WatchMatches (WatchedItemId, PostId) VALUES ({bad}, 1), ({good}, 2);");

        Assert.Equal(["unknown_artist"], _watches.RemoveNonArtistWatches());
        Assert.Equal(0, _db.Scalar(_db.Paths.AppDatabasePath, $"SELECT COUNT(*) FROM WatchedItems WHERE Id = {bad};"));
        Assert.Equal(0, _db.Scalar(_db.Paths.AppDatabasePath, $"SELECT COUNT(*) FROM WatchMatches WHERE WatchedItemId = {bad};"));
        Assert.Equal(1, _db.Scalar(_db.Paths.AppDatabasePath, $"SELECT COUNT(*) FROM WatchedItems WHERE Id = {good};"));
        Assert.Empty(_watches.RemoveNonArtistWatches()); // nothing left to remove
    }
}
