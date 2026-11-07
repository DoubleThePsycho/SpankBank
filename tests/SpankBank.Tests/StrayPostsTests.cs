using SpankBank.Core.Models;
using SpankBank.Data.Repositories;
using SpankBank.Data.RubbishBin;

namespace SpankBank.Tests;

public class StrayPostsTests : IDisposable
{
    private readonly TempDatabases _db = new();
    private readonly WatchesRepository _watches;
    private readonly BinStore _bin;
    private readonly StrayPosts _strays;
    private readonly string _media = Path.Combine(Path.GetTempPath(), "spankbank-stray-" + Guid.NewGuid().ToString("N"));
    private readonly long _watch;

    public StrayPostsTests()
    {
        _watches = new WatchesRepository(_db.Paths.AppDatabasePath, _db.Paths.ServerDatabasePath);
        _bin = new BinStore(_db.Paths);
        _strays = new StrayPosts(_db.Paths, _bin);
        _watch = _watches.Add(WatchedItemType.Artist, "alpha", "alpha")!.Value;
        // Local posts 1..5. 1 is matched by an enabled watch, 2 is a favorite, 3,4,5 are loose.
        for (var i = 1; i <= 5; i++)
            _db.Execute(_db.Paths.DeviceDatabasePath, $"INSERT INTO Posts (Id, CreatedAt, UpdatedAt, Md5, FileExt, SavedAt, Status) VALUES ({i}, '2024-01-01', '2024-01-01', '{new string((char)('a' + i), 32)}', 'png', '2026-01-01T00:00:00+00:00', 1);");
        _db.Execute(_db.Paths.AppDatabasePath, $"INSERT INTO WatchMatches (WatchedItemId, PostId) VALUES ({_watch}, 1);");
        _db.Execute(_db.Paths.AppDatabasePath, "INSERT INTO Favorites (PostId, CreatedAt) VALUES (2, '2026-01-01');");
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_media, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void Matches_can_be_looked_up_by_post_so_the_stray_count_does_not_scan_every_watch()
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_db.Paths.AppDatabasePath}");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'index' AND name = 'IX_WatchMatches_PostId';";
        Assert.NotNull(cmd.ExecuteScalar());
    }

    private long[] Listed(string query) => new DownloadedPostSearch(_db.Paths).Search(query, 1, 50).Items.Select(i => i.Id).Order().ToArray();

    [Fact] public void Posts_nothing_keeps_are_strays() => Assert.Equal(3, _strays.Count());

    [Fact] public void The_search_term_lists_them() { Assert.Equal([3L, 4L, 5L], Listed("stray:true")); Assert.Equal([1L, 2L], Listed("stray:false")); }

    [Fact] public void A_disabled_watch_no_longer_keeps_its_posts() { _watches.SetEnabled(_watch, false); Assert.Equal([1L, 3L, 4L, 5L], Listed("stray:true")); }

    [Fact] public void Starring_a_stray_rescues_it() { _db.Execute(_db.Paths.AppDatabasePath, "INSERT INTO Favorites (PostId, CreatedAt) VALUES (3, '2026-01-01');"); Assert.Equal([4L, 5L], Listed("stray:true")); }

    [Fact]
    public void Bin_all_moves_only_strays_and_they_leave_no_tombstone_after_the_purge()
    {
        Assert.Equal(3, _strays.BinAll());
        Assert.Equal(0, _strays.Count());
        Assert.Equal([1L, 2L], Listed(""));

        _bin.Process(_media, DateTimeOffset.UtcNow);
        _bin.Process(_media, DateTimeOffset.UtcNow.AddDays(31));
        Assert.Equal(0, _db.Scalar(_db.Paths.AppDatabasePath, "SELECT COUNT(*) FROM DeletedPosts;"));
    }

    [Fact] public void Running_it_twice_bins_nothing_more() { _strays.BinAll(); Assert.Equal(0, _strays.BinAll()); }
}
