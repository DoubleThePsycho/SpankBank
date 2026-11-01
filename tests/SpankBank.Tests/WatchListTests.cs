using SpankBank.Core.Models;
using SpankBank.Data.Repositories;

namespace SpankBank.Tests;

public class WatchListTests : IDisposable
{
    private readonly TempDatabases _db = new();
    private readonly WatchesRepository _watches;

    public WatchListTests()
    {
        _watches = new WatchesRepository(_db.Paths.AppDatabasePath, _db.Paths.ServerDatabasePath);
    }

    public void Dispose() => _db.Dispose();

    private void App(string sql) => _db.Execute(_db.Paths.AppDatabasePath, sql);
    private void Device(string sql) => _db.Execute(_db.Paths.DeviceDatabasePath, sql);
    private void Server(string sql) => _db.Execute(_db.Paths.ServerDatabasePath, sql);

    private long Artist(string name, int matchCount = 0)
    {
        var id = _watches.Add(WatchedItemType.Artist, name, name)!.Value;
        App($"UPDATE WatchedItems SET MatchCount = {matchCount}, LastCheckedAt = '2026-01-01' WHERE Id = {id};");
        return id;
    }

    private void Local(long post, int status) => Device($"INSERT INTO Posts (Id, CreatedAt, UpdatedAt, Md5, FileExt, SavedAt, Status) VALUES ({post}, '2024-01-01', '2024-01-01', 'm{post}', 'png', '2026-01-01', {status});");
    private void Match(long watch, params long[] posts) { foreach (var p in posts) App($"INSERT INTO WatchMatches (WatchedItemId, PostId) VALUES ({watch}, {p});"); }
    private void Failed(long post, string error) => App($"INSERT INTO DownloadAttempts (PostId, Attempts, LastAttemptAt, NextAttemptAt, LastError) VALUES ({post}, 1, '2026-01-01', '2026-02-01', '{error}');");

    [Fact]
    public void Stats_split_downloaded_restored_and_lost()
    {
        var a = Artist("alpha");
        Local(1, 1); Local(2, 1); Local(3, 2); Local(4, 3);   // 2 downloaded, original + sample restored
        Match(a, 1, 2, 3, 4, 5, 6, 7);
        Failed(5, "Deleted on e621 and not found in the Wayback Machine.");
        Failed(6, "The catalog has no file for this post.");
        Failed(7, "HTTP 503"); // a transient failure is not "lost": it is still pending

        var stats = _watches.GetPostStats(WatchedItemType.Artist)[a];
        Assert.Equal(new WatchPostStats(Downloaded: 2, Restored: 2, Lost: 2, Pending: 1), stats);
    }

    [Fact]
    public void Pending_leaves_out_blacklisted_binned_and_untouched_counts_as_pending()
    {
        var a = Artist("alpha");
        Match(a, 1, 2, 3, 4);
        App("INSERT INTO BlockedPosts (PostId) VALUES (2);");
        App("INSERT INTO DeletedPosts (PostId, DeletedAt, State, Reason) VALUES (3, '2026-01-01', 1, 'user');");

        // 1 and 4 were never tried; 2 is blacklisted and 3 is in the bin.
        Assert.Equal(2, _watches.GetPostStats(WatchedItemType.Artist)[a].Pending);
    }

    [Fact]
    public void A_post_matched_by_two_artists_counts_for_each_and_binned_posts_are_not_local()
    {
        var a = Artist("alpha"); var b = Artist("beta");
        Local(1, 1);
        Match(a, 1); Match(b, 1);
        var stats = _watches.GetPostStats(WatchedItemType.Artist);
        Assert.Equal(1, stats[a].Downloaded);
        Assert.Equal(1, stats[b].Downloaded);

        Device("UPDATE Posts SET SavedAt = NULL WHERE Id = 1;"); // hidden by the bin
        Assert.Equal(0, _watches.GetPostStats(WatchedItemType.Artist)[a].Downloaded);
    }

    [Fact]
    public void The_artist_page_sorts_by_any_column_and_pages()
    {
        var c = Artist("charlie", 5); var a = Artist("alpha", 50); var b = Artist("bravo", 20);
        Local(1, 1); Match(b, 1);

        Assert.Equal(["alpha", "bravo", "charlie"], Names(_watches.GetArtistPage(null, false, 1, 10)));
        Assert.Equal(["alpha", "bravo", "charlie"], Names(_watches.GetArtistPage("total", true, 1, 10)));
        Assert.Equal(["charlie", "alpha", "bravo"], Names(_watches.GetArtistPage("id", false, 1, 10)));
        Assert.Equal("bravo", Names(_watches.GetArtistPage("downloaded", true, 1, 10))[0]);

        var paged = _watches.GetArtistPage("name", false, 2, 2);
        Assert.Equal(["charlie"], Names(paged));
        Assert.Equal((3, 2, 2), (paged.Total, paged.LastPage, paged.Page));
    }

    private static string[] Names(WatchPage<ArtistListItem> page) => page.Items.Select(i => i.Entry.SourceValue).ToArray();

    private long Pool(long poolId, string name, string category, int pages, bool enabled = true, string? origin = null)
    {
        Server($"INSERT INTO Pools (Id, Name, CreatedAt, UpdatedAt, IsActive, Category, PostIds, PostCount) VALUES ({poolId}, '{name}', '2024-01-01', '2024-01-01', 1, '{category}', '', {pages});");
        var id = _watches.Add(WatchedItemType.Pool, poolId.ToString(), $"Pool #{poolId}", origin)!.Value;
        if (!enabled) App($"UPDATE WatchedItems SET Enabled = 0 WHERE Id = {id};");
        return id;
    }

    [Fact]
    public void Pools_default_to_the_highest_id_first_and_hide_disabled_ones()
    {
        Pool(5, "Old_Pool", "series", 3); Pool(90, "New_Pool", "collection", 7); Pool(40, "Off_Pool", "series", 1, enabled: false);

        Assert.Equal(["90", "5"], _watches.GetPoolPage(null, true, 1, 50, includeDisabled: false).Items.Select(i => i.Entry.SourceValue));
        Assert.Equal(["90", "40", "5"], _watches.GetPoolPage(null, true, 1, 50, includeDisabled: true).Items.Select(i => i.Entry.SourceValue));
        Assert.Equal(1, _watches.CountDisabledPools());
    }

    [Fact]
    public void Pools_carry_names_page_counts_and_local_counts_and_sort_on_them()
    {
        var p1 = Pool(1, "Zeta_Story", "series", 10); var p2 = Pool(2, "Alpha_Story", "series", 30);
        Local(100, 1); Local(101, 2); Match(p1, 100, 101, 102);

        var page = _watches.GetPoolPage("name", false, 1, 50, true);
        Assert.Equal(["Alpha Story", "Zeta Story"], page.Items.Select(i => i.Entry.DisplayName));
        Assert.Equal([30, 10], page.Items.Select(i => i.PagesInExport));
        Assert.Equal("1", _watches.GetPoolPage("pages", false, 1, 50, true).Items[0].Entry.SourceValue);
        var local = _watches.GetPoolPage("local", true, 1, 50, true).Items;
        Assert.Equal(2, local[0].Stats.Local); // Zeta: one downloaded, one restored
        Assert.Equal(p1, local[0].Entry.Id);
    }

    [Fact]
    public void Pool_pages_slice_the_list()
    {
        for (var i = 1; i <= 25; i++) Pool(i, $"P{i}", "series", i);
        var page = _watches.GetPoolPage(null, true, 3, 10, true);
        Assert.Equal(["5", "4", "3", "2", "1"], page.Items.Select(i => i.Entry.SourceValue));
        Assert.Equal((25, 3), (page.Total, page.LastPage));
    }

    [Fact]
    public void Pools_added_for_marker_artists_are_cleaned_up_but_real_artists_keep_theirs()
    {
        Pool(1, "A", "series", 1, origin: "auto:unknown_artist");
        Pool(2, "B", "series", 1, origin: "suggested:conditional_dnp");
        Pool(3, "C", "series", 1, origin: "auto:loimu");
        Pool(4, "D", "series", 1, origin: null);

        Assert.Equal(2, _watches.RemovePoolsOfNonArtists());
        Assert.Equal(["4", "3"], _watches.GetPoolPage(null, true, 1, 50, true).Items.Select(i => i.Entry.SourceValue));
        Assert.Equal(0, _watches.RemovePoolsOfNonArtists());
    }

    [Fact]
    public void An_artists_own_pools_are_listed_with_their_stats()
    {
        var pool = Pool(7, "Mine", "series", 4, origin: "auto:alpha"); Pool(8, "Theirs", "series", 4, origin: "auto:beta");
        Local(1, 1); Match(pool, 1);

        var pools = _watches.GetArtistPools("alpha");
        Assert.Equal(["7"], pools.Select(p => p.Entry.SourceValue));
        Assert.Equal(1, pools[0].Stats.Downloaded);
    }

    [Fact]
    public void Characters_split_into_found_enabled_and_custom_and_sort_on_them()
    {
        var a = Artist("alpha"); var b = Artist("bravo");
        App($"""
            INSERT INTO WatchedArtistTags (WatchedItemId, Tag, Kind, IsManual, Enabled) VALUES
                ({a}, 'x_(alpha)', 1, 0, 1), ({a}, 'y_(alpha)', 1, 0, 0), ({a}, 'mine_(alpha)', 1, 1, 1),
                ({b}, 'z_(bravo)', 1, 0, 1), ({a}, 'old', 0, 0, 1);
            """);

        var alpha = _watches.GetArtistPage(null, false, 1, 10).Items.Single(i => i.Entry.SourceValue == "alpha");
        Assert.Equal((2, 2, 1, 1), (alpha.Found, alpha.Enabled, alpha.Custom, alpha.Aliases));
        Assert.Equal("alpha", Names(_watches.GetArtistPage("custom", true, 1, 10))[0]);
        Assert.Equal("alpha", Names(_watches.GetArtistPage("found", true, 1, 10))[0]);
    }

    [Fact]
    public void The_online_artist_list_sorts_by_name_or_post_count_in_either_direction()
    {
        Server("INSERT INTO Tags (Id, Name, Category, PostCount) VALUES (1, 'bravo', 1, 50), (2, 'alpha', 1, 90), (3, 'charlie', 1, 10);");
        var catalog = new CatalogBrowser(_db.Paths.ServerDatabasePath);

        Assert.Equal(["alpha", "bravo", "charlie"], catalog.SearchArtists(null, "posts", true, 1, 10, descending: true).Rows.Select(r => r.Name));
        Assert.Equal(["charlie", "bravo", "alpha"], catalog.SearchArtists(null, "posts", true, 1, 10, descending: false).Rows.Select(r => r.Name));
        Assert.Equal(["alpha", "bravo", "charlie"], catalog.SearchArtists(null, "name", true, 1, 10, descending: false).Rows.Select(r => r.Name));
        Assert.Equal(["charlie", "bravo", "alpha"], catalog.SearchArtists(null, "name", true, 1, 10, descending: true).Rows.Select(r => r.Name));

        var byId = catalog.SearchArtists(null, "id", true, 1, 10, descending: true).Rows;
        Assert.Equal([3L, 2L, 1L], byId.Select(r => r.Id));
        Assert.Equal("charlie", byId[0].Name);
        Assert.Equal(2, catalog.GetArtist("alpha")!.Id);
    }
}
