using SpankBank.Core.Models;
using SpankBank.Data.Repositories;
using SpankBank.Data.RubbishBin;

namespace SpankBank.Tests;

public class ArtistRemovalTests : IDisposable
{
    private readonly TempDatabases _db = new();
    private readonly WatchesRepository _watches;
    private readonly BinStore _bin;
    private readonly ArtistPostRemoval _removal;
    private readonly long _a, _b;
    private readonly string _media = Path.Combine(Path.GetTempPath(), "spankbank-rm-" + Guid.NewGuid().ToString("N"));

    public ArtistRemovalTests()
    {
        _watches = new WatchesRepository(_db.Paths.AppDatabasePath, _db.Paths.ServerDatabasePath);
        _bin = new BinStore(_db.Paths);
        _removal = new ArtistPostRemoval(_db.Paths, _bin);
        _a = _watches.Add(WatchedItemType.Artist, "alpha", "alpha")!.Value;
        _b = _watches.Add(WatchedItemType.Artist, "beta", "beta")!.Value;
        // Posts 1..5 are local. alpha matches 1-4 and 6 (not downloaded); beta matches 2; post 3 is a favorite.
        for (var i = 1; i <= 5; i++)
            _db.Execute(_db.Paths.DeviceDatabasePath, $"INSERT INTO Posts (Id, CreatedAt, UpdatedAt, Md5, FileExt, SavedAt, Status) VALUES ({i}, '2024-01-01', '2024-01-01', '{new string((char)('a' + i), 32)}', 'png', '2026-01-01T00:00:00+00:00', 1);");
        foreach (var post in new[] { 1, 2, 3, 4, 6 }) _db.Execute(_db.Paths.AppDatabasePath, $"INSERT INTO WatchMatches (WatchedItemId, PostId) VALUES ({_a}, {post});");
        _db.Execute(_db.Paths.AppDatabasePath, $"INSERT INTO WatchMatches (WatchedItemId, PostId) VALUES ({_b}, 2);");
        _db.Execute(_db.Paths.AppDatabasePath, "INSERT INTO Favorites (PostId, CreatedAt) VALUES (3, '2026-01-01');");
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_media, recursive: true); } catch (IOException) { }
    }

    private List<long> Requested()
    {
        var ids = new List<long>();
        for (long i = 1; i <= 6; i++)
            if (_db.Scalar(_db.Paths.AppDatabasePath, $"SELECT COUNT(*) FROM DeletedPosts WHERE PostId = {i};") > 0) ids.Add(i);
        return ids;
    }

    [Fact]
    public void Counts_separate_local_shared_and_favorite_posts()
        => Assert.Equal(new ArtistRemovalCounts(Total: 4, Exclusive: 2, Removable: 3, Favorites: 1), _removal.Counts(_a));

    [Fact]
    public void An_artist_with_nothing_downloaded_has_no_counts()
    {
        var gamma = _watches.Add(WatchedItemType.Artist, "gamma", "gamma")!.Value;
        _db.Execute(_db.Paths.AppDatabasePath, $"INSERT INTO WatchMatches (WatchedItemId, PostId) VALUES ({gamma}, 6);"); // matched, not downloaded
        Assert.Equal(new ArtistRemovalCounts(0, 0, 0, 0), _removal.Counts(gamma));
    }

    [Fact]
    public void Remove_all_bins_everything_but_favorites()
    {
        Assert.Equal(3, _removal.Remove(_a, exclusiveOnly: false));
        Assert.Equal([1L, 2L, 4L], Requested());
    }

    [Fact]
    public void Remove_exclusive_keeps_posts_another_watch_matches()
    {
        Assert.Equal(2, _removal.Remove(_a, exclusiveOnly: true));
        Assert.Equal([1L, 4L], Requested()); // 2 belongs to beta too, 3 is a favorite
    }

    [Fact]
    public void Disabled_watches_do_not_count_as_other_owners()
    {
        _watches.SetEnabled(_b, false);
        Assert.Equal(3, _removal.Counts(_a).Exclusive);
    }

    [Fact]
    public void Posts_removed_with_their_artist_leave_no_tombstone_after_the_purge()
    {
        _removal.Remove(_a, exclusiveOnly: false);
        _bin.Process(_media, DateTimeOffset.UtcNow);
        _bin.Process(_media, DateTimeOffset.UtcNow.AddDays(31));

        Assert.Empty(Requested()); // rows are gone: the artist can be added again and the posts download again
    }

    [Fact]
    public void Posts_the_user_deleted_keep_their_tombstone_after_the_purge()
    {
        _bin.RequestDelete(1);
        _bin.Process(_media, DateTimeOffset.UtcNow);
        _bin.Process(_media, DateTimeOffset.UtcNow.AddDays(31));

        Assert.Equal([1L], Requested());
    }

    [Fact]
    public void Already_binned_posts_are_not_counted_again()
    {
        _bin.RequestDelete(1);
        Assert.Equal(3, _removal.Counts(_a).Total); // 2, 3, 4
    }
}
