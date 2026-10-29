using SpankBank.Core.Models;
using SpankBank.Data.Matching;
using SpankBank.Data.Repositories;

namespace SpankBank.Tests;

public class ArtistFilterTests : IDisposable
{
    private readonly TempDatabases _db = new();
    private readonly WatchesRepository _watches;
    private readonly long _alpha;

    public ArtistFilterTests()
    {
        _watches = new WatchesRepository(_db.Paths.AppDatabasePath, _db.Paths.ServerDatabasePath);
        _db.Execute(_db.Paths.ServerDatabasePath, """
            INSERT INTO Tags (Id, Name, Category, PostCount) VALUES (1, 'alpha', 1, 6), (2, 'canine', 0, 3), (3, 'feline', 0, 3);
            INSERT INTO Posts (Id, CreatedAt, UpdatedAt, TagString, Rating) VALUES
                (1, '2024-01-01', '2024-01-01', 'alpha canine', 0),
                (2, '2024-01-01', '2024-01-01', 'alpha canine', 1),
                (3, '2024-01-01', '2024-01-01', 'alpha feline', 0),
                (4, '2024-01-01', '2024-01-01', 'alpha feline', 1),
                (5, '2024-01-01', '2024-01-01', 'alpha', 0),
                (6, '2024-01-01', '2024-01-01', 'beta canine', 0);
            """);
        _alpha = _watches.Add(WatchedItemType.Artist, "alpha", "alpha")!.Value;
    }

    public void Dispose() => _db.Dispose();

    private long[] Matched()
    {
        new WatchMatcher(_db.Paths).Run(all: true);
        var ids = new List<long>();
        for (long i = 1; i <= 6; i++)
            if (_db.Scalar(_db.Paths.AppDatabasePath, $"SELECT COUNT(*) FROM WatchMatches WHERE WatchedItemId = {_alpha} AND PostId = {i};") > 0) ids.Add(i);
        return ids.ToArray();
    }

    [Fact] public void Without_filters_every_post_of_the_artist_matches() => Assert.Equal([1L, 2L, 3L, 4L, 5L], Matched());

    [Fact]
    public void A_whitelist_line_is_one_expression_and_posts_need_one_of_them()
    {
        Assert.Null(_watches.SetFilters(_alpha, "rating:s canine\nfeline rating:q", ""));
        Assert.Equal([1L, 4L], Matched()); // 2 is canine but q, 3 is feline but s, 5 matches neither
    }

    [Fact]
    public void A_blacklist_line_rejects_a_post_when_the_whole_line_matches()
    {
        Assert.Null(_watches.SetFilters(_alpha, "", "rating:q canine"));
        Assert.Equal([1L, 3L, 4L, 5L], Matched()); // only post 2 is both q and canine
    }

    [Fact]
    public void Whitelist_and_blacklist_combine()
    {
        Assert.Null(_watches.SetFilters(_alpha, "canine\nfeline", "rating:q"));
        Assert.Equal([1L, 3L], Matched());
    }

    [Fact]
    public void Clearing_the_filters_brings_the_posts_back()
    {
        _watches.SetFilters(_alpha, "canine", "");
        Assert.Equal([1L, 2L], Matched());
        _watches.SetFilters(_alpha, "", "");
        Assert.Equal([1L, 2L, 3L, 4L, 5L], Matched());
    }

    [Fact]
    public void Saving_filters_flags_the_watch_for_matching_again()
    {
        _db.Execute(_db.Paths.AppDatabasePath, $"UPDATE WatchedItems SET LastCheckedAt = '2026-01-01' WHERE Id = {_alpha};");
        _watches.SetFilters(_alpha, "canine", "");
        Assert.Equal(1, new WatchMatcher(_db.Paths).CountChanged());
    }

    [Fact]
    public void Lines_are_cleaned_and_a_bad_line_rejects_the_whole_save()
    {
        Assert.Null(_watches.SetFilters(_alpha, "  rating:s   canine \n\n RATING:S canine\n", " feline "));
        var saved = _watches.GetArtist("alpha")!;
        Assert.Equal("rating:s canine", saved.Whitelist);
        Assert.Equal("feline", saved.Blacklist);

        Assert.NotNull(_watches.SetFilters(_alpha, "canine", "fav:true"));
        Assert.Equal("rating:s canine", _watches.GetArtist("alpha")!.Whitelist); // nothing changed
    }

    [Fact]
    public void A_new_artist_can_be_added_disabled_and_is_not_matched()
    {
        var gamma = _watches.Add(WatchedItemType.Artist, "gamma", "gamma", enabled: false)!.Value;
        Assert.False(_watches.GetArtist("gamma")!.Enabled);
        Assert.Equal(0, new WatchMatcher(_db.Paths).CountChanged() - 1 + 0 * gamma); // only alpha is waiting
    }
}
