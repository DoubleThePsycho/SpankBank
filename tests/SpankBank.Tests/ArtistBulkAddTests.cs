using SpankBank.Core.Models;
using SpankBank.Data.Repositories;

namespace SpankBank.Tests;

public class ArtistBulkAddTests : IDisposable
{
    private readonly TempDatabases _db = new();
    private readonly WatchesRepository _watches;

    public ArtistBulkAddTests()
    {
        _watches = new WatchesRepository(_db.Paths.AppDatabasePath, _db.Paths.ServerDatabasePath);
        var s = _db.Paths.ServerDatabasePath;
        _db.Execute(s, """
            INSERT INTO Tags (Id, Name, Category, PostCount) VALUES
                (1, 'loimu', 1, 500), (2, 'wolfy-nail', 1, 900), (3, 'wolfy-nail_(artist_alt)', 1, 5),
                (4, 'not_an_artist', 0, 100), (5, 'zero_posts_artist', 1, 0);
            INSERT INTO TagAliases (Id, AntecedentName, ConsequentName, Status) VALUES
                (1, 'wolfynail', 'wolfy-nail', 'active'), (2, 'old_name', 'loimu', 'retired');
            """);
    }

    public void Dispose() => _db.Dispose();

    private long WatchCount() => _db.Scalar(_db.Paths.AppDatabasePath, "SELECT COUNT(*) FROM WatchedItems WHERE ItemType = 2;");

    [Fact]
    public void Names_split_on_lines_commas_and_semicolons_and_collapse_duplicates()
    {
        var names = ArtistBulkAdd.ParseNames("Loimu, wolfy-nail;\r\n  artist:Loimu \n\n  ");
        Assert.Equal(["Loimu", "wolfy-nail"], names);
    }

    [Fact]
    public void Adds_known_artists_and_returns_the_rest()
    {
        var result = ArtistBulkAdd.Run(_watches, "loimu\nnot_an_artist\nnobody_here");

        Assert.Equal(["loimu"], result.Added);
        Assert.Equal(["not_an_artist", "nobody_here"], result.Unrecognized.Select(u => u.Input));
        Assert.Equal(1, WatchCount());
    }

    [Fact]
    public void Aliases_resolve_to_the_artist_and_two_names_for_one_artist_add_it_once()
    {
        var result = ArtistBulkAdd.Run(_watches, "wolfynail, wolfy-nail");

        Assert.Equal(["wolfy-nail"], result.Added);
        Assert.Empty(result.AlreadyWatched);
        Assert.Equal(1, WatchCount());
    }

    [Fact]
    public void Already_watched_artists_are_reported_not_duplicated()
    {
        ArtistBulkAdd.Run(_watches, "loimu");
        var again = ArtistBulkAdd.Run(_watches, "loimu, wolfy-nail");

        Assert.Equal(["wolfy-nail"], again.Added);
        Assert.Equal(["loimu"], again.AlreadyWatched);
        Assert.Equal(2, WatchCount());
    }

    [Fact]
    public void A_retired_alias_is_not_resolved()
    {
        var result = ArtistBulkAdd.Run(_watches, "old_name");
        Assert.Empty(result.Added);
        Assert.Single(result.Unrecognized);
    }

    [Fact]
    public void Unrecognized_names_get_prefix_suggestions_for_artists_with_posts()
    {
        var result = ArtistBulkAdd.Run(_watches, "wolfy\nzero_posts");

        var wolfy = result.Unrecognized.Single(u => u.Input == "wolfy");
        Assert.Equal(["wolfy-nail", "wolfy-nail_(artist_alt)"], wolfy.Suggestions);
        Assert.Empty(result.Unrecognized.Single(u => u.Input == "zero_posts").Suggestions);
    }

    [Fact]
    public void Spaces_are_treated_as_underscores()
    {
        var result = ArtistBulkAdd.Run(_watches, "Zero Posts Artist");
        Assert.Equal(["zero_posts_artist"], result.Added); // same rule as the single add: any artist tag in the catalog
    }

    [Fact]
    public void A_whole_paste_asks_for_one_match()
    {
        _db.Execute(_db.Paths.AppDatabasePath, "DELETE FROM Settings WHERE Key = 'Worker.MatchRequested';");
        ArtistBulkAdd.Run(_watches, "loimu, wolfy-nail");

        Assert.Equal(1, _db.Scalar(_db.Paths.AppDatabasePath, "SELECT COUNT(*) FROM Settings WHERE Key = 'Worker.MatchRequested' AND Value = 'true';"));
    }
}
