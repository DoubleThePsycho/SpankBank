using SpankBank.Core.Models;
using SpankBank.Core.Search;
using SpankBank.Data.Blacklist;
using SpankBank.Data.Downloads;
using SpankBank.Data.Repositories;

namespace SpankBank.Tests;

public class BlacklistRulesTests
{
    private static PostMatchRow Row(int rating = 0, int score = 0, bool deleted = false)
        => new(1, rating, score, 0, "png", 10, 10, null, 100, deleted, 0, "2024-01-01", null, 3);

    private static bool Matches(string entry, string tags, PostMatchRow? row = null)
        => BlacklistRules.Create([entry]).Matches(tags, row ?? Row());

    [Fact] public void A_single_tag_matches_whole_tags_only() { Assert.True(Matches("gore", "a gore b")); Assert.False(Matches("gore", "a gorebag b")); }
    [Fact] public void All_required_tags_must_be_present() { Assert.True(Matches("a b", "b a c")); Assert.False(Matches("a b", "a c")); }
    [Fact] public void Excluded_tags_veto_the_entry() { Assert.True(Matches("gore -comic", "gore")); Assert.False(Matches("gore -comic", "gore comic")); }
    [Fact] public void Optional_tags_need_one() { Assert.True(Matches("~a ~b", "b")); Assert.False(Matches("~a ~b", "c")); }
    [Fact] public void Each_parenthesised_group_needs_one_of_its_own_tags()
    {
        const string entry = "( ~a ~b ) ( ~c ~d )";
        Assert.True(Matches(entry, "a d"));
        Assert.True(Matches(entry, "b c"));
        Assert.False(Matches(entry, "a b")); // the second group has nothing
        Assert.False(Matches(entry, "c d")); // the first group has nothing
    }
    [Fact] public void Loose_optional_tags_next_to_a_group_form_one_more_group() { Assert.True(Matches("( ~a ~b ) ~c ~d", "a d")); Assert.False(Matches("( ~a ~b ) ~c ~d", "a b")); }
    [Theory]
    [InlineData("( ~a ~b")]
    [InlineData("~a ~b )")]
    [InlineData("( ( ~a ) )")]
    public void Unbalanced_parentheses_are_an_error(string entry) => Assert.NotNull(BlacklistRules.Validate(entry));
    [Fact] public void Wildcards_match_any_tag() { Assert.True(Matches("*_(artist)", "x_(artist) y")); Assert.False(Matches("*_(artist)", "x y")); }
    [Fact] public void Metatags_filter_by_column() { Assert.True(Matches("rating:e", "a", Row(rating: 2))); Assert.False(Matches("rating:e", "a", Row(rating: 0))); }
    [Fact] public void Deleted_posts_match_unless_the_entry_says_otherwise() { Assert.True(Matches("gore", "gore", Row(deleted: true))); Assert.False(Matches("gore status:active", "gore", Row(deleted: true))); }
    [Fact] public void Any_entry_is_enough() { var rules = BlacklistRules.Create(["a", "b"]); Assert.True(rules.Matches("b", Row())); Assert.False(rules.Matches("c", Row())); }
    [Fact] public void An_empty_list_blocks_nothing() => Assert.False(BlacklistRules.Create([]).Matches("anything", Row()));

    [Theory]
    [InlineData("fav:true")]
    [InlineData("pool:5")]
    [InlineData("isparent:true")]
    [InlineData("bogus_meta:1")]
    public void Terms_only_the_posts_page_understands_are_rejected(string entry) => Assert.NotNull(BlacklistRules.Validate(entry));

    [Fact] public void Plain_entries_are_valid() => Assert.Null(BlacklistRules.Validate("gore -comic rating:e"));
}

public class BlockedPostsTests : IDisposable
{
    private readonly TempDatabases _db = new();
    private readonly BlacklistRepository _blacklist;
    private readonly BlockedPostsRefresher _refresher;
    private readonly DownloadStore _store;
    private readonly WatchesRepository _watches;

    public BlockedPostsTests()
    {
        _blacklist = new BlacklistRepository(_db.Paths.AppDatabasePath);
        _refresher = new BlockedPostsRefresher(_db.Paths);
        _store = new DownloadStore(_db.Paths);
        _watches = new WatchesRepository(_db.Paths.AppDatabasePath, _db.Paths.ServerDatabasePath);
        _db.Execute(_db.Paths.ServerDatabasePath, """
            INSERT INTO Posts (Id, CreatedAt, UpdatedAt, TagString, Rating) VALUES
                (1, '2024-01-01', '2024-01-01', 'cat gore', 0),
                (2, '2024-01-01', '2024-01-01', 'cat', 0),
                (3, '2024-01-01', '2024-01-01', 'dog gore', 0),
                (4, '2024-01-01', '2024-01-01', 'dog', 0);
            """);
    }

    public void Dispose() => _db.Dispose();

    private long Watch(string name, bool ignore = false)
    {
        var id = _watches.Add(WatchedItemType.Search, name, name)!.Value;
        if (ignore) _watches.SetIgnoreBlacklist(id, true);
        return id;
    }

    private void Match(long watch, params long[] posts)
    {
        foreach (var post in posts)
            _db.Execute(_db.Paths.AppDatabasePath, $"INSERT INTO WatchMatches (WatchedItemId, PostId) VALUES ({watch}, {post});");
    }

    private List<long> Blocked()
    {
        var ids = new List<long>();
        for (long i = 1; i <= 4; i++)
            if (_db.Scalar(_db.Paths.AppDatabasePath, $"SELECT COUNT(*) FROM BlockedPosts WHERE PostId = {i};") > 0) ids.Add(i);
        return ids;
    }

    [Fact]
    public void Matching_pending_posts_are_blocked_and_leave_the_queue()
    {
        Match(Watch("w"), 1, 2, 3, 4);
        Assert.Null(_blacklist.SetAll(["gore"]));
        _refresher.Refresh();

        Assert.Equal([1L, 3L], Blocked());
        Assert.Equal([2L, 4L], _store.NextPending(10, DateTimeOffset.UtcNow).Order().ToArray());
        Assert.Equal(2, _store.CountPending(DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Removing_an_entry_unblocks_its_posts()
    {
        Match(Watch("w"), 1, 2);
        _blacklist.SetAll(["gore"]);
        _refresher.Refresh();
        Assert.Equal([1L], Blocked());

        _blacklist.SetAll([]);
        _refresher.Refresh();
        Assert.Empty(Blocked());
        Assert.Contains(1L, _store.NextPending(10, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void A_watch_that_ignores_the_blacklist_still_gets_its_posts()
    {
        Match(Watch("normal"), 1, 3);
        Match(Watch("override", ignore: true), 1);
        _blacklist.SetAll(["gore"]);
        _refresher.Refresh();

        Assert.Equal([3L], Blocked()); // post 1 is also matched by the overriding watch
        Assert.Contains(1L, _store.NextPending(10, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Posts_already_on_the_device_are_blocked_too_so_they_are_hidden()
    {
        _db.Execute(_db.Paths.DeviceDatabasePath, """
            INSERT INTO Posts (Id, CreatedAt, UpdatedAt, Md5, SavedAt, Status) VALUES (1, '2024-01-01', '2024-01-01', 'm', '2024-01-02', 1);
            """);
        _blacklist.SetAll(["gore"]);
        _refresher.Refresh();

        Assert.Equal([1L], Blocked());
        var (pending, local) = _blacklist.CountBlocked(_db.Paths.DeviceDatabasePath);
        Assert.Equal((0, 1), (pending, local));
    }

    [Fact]
    public void Listings_hide_blocked_posts_unless_asked()
    {
        foreach (var id in new[] { 1, 2 })
            _db.Execute(_db.Paths.DeviceDatabasePath, $"INSERT INTO Posts (Id, CreatedAt, UpdatedAt, Md5, SavedAt, Status) VALUES ({id}, '2024-01-01', '2024-01-01', 'm{id}', '2024-01-02', 1);");
        _blacklist.SetAll(["gore"]);
        _refresher.Refresh();
        var search = new DownloadedPostSearch(_db.Paths);

        Assert.Equal([2L], search.Search("", 1, 50).Items.Select(i => i.Id));
        Assert.Equal([1L], search.Search("blacklisted:true", 1, 50).Items.Select(i => i.Id));
        Assert.Equal(2, search.Search("blacklisted:any", 1, 50).Items.Count);
    }

    [Fact]
    public void Saving_validates_and_cleans_the_list()
    {
        Assert.NotNull(_blacklist.SetAll(["fav:true"]));
        Assert.Null(_blacklist.SetAll(["  gore   -comic ", "", "GORE -comic", "rating:e"]));
        Assert.Equal(["gore -comic", "rating:e"], _blacklist.GetAll());
    }

    [Fact]
    public void A_failed_save_keeps_the_old_list()
    {
        _blacklist.SetAll(["gore"]);
        Assert.NotNull(_blacklist.SetAll(["ok", "pool:3"]));
        Assert.Equal(["gore"], _blacklist.GetAll());
    }
}
