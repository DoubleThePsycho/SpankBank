using SpankBank.Core.Models;
using SpankBank.Data.Downloads;
using SpankBank.Data.Repositories;

namespace SpankBank.Tests;

public class DownloadStoreTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly TempDatabases _db = new();
    private readonly DownloadStore _store;

    public DownloadStoreTests()
    {
        _store = new DownloadStore(_db.Paths);
        var watches = new WatchesRepository(_db.Paths.AppDatabasePath, _db.Paths.ServerDatabasePath);
        var watch = watches.Add(WatchedItemType.Artist, "someone", "someone")!.Value;
        foreach (var id in new[] { 1, 2, 3 })
            _db.Execute(_db.Paths.AppDatabasePath, $"INSERT INTO WatchMatches (WatchedItemId, PostId) VALUES ({watch}, {id});");
    }

    public void Dispose() => _db.Dispose();

    private static CatalogPost Post(long id) => new(id, new string('a', 32), "png", false, 0, 0, 10, 10, 100,
        "2026-01-01", "2026-01-01", "tag", null, null, null);

    [Fact]
    public void Matched_posts_are_pending_until_saved()
    {
        Assert.Equal(3, _store.CountPending(Now));
        _store.MarkSaved(Post(1), LocalPostStatus.Downloaded);
        Assert.Equal(2, _store.CountPending(Now));
        Assert.DoesNotContain(1L, _store.NextPending(10, Now));
    }

    [Fact]
    public void Posts_the_export_marks_deleted_come_after_the_others()
    {
        foreach (var id in new[] { 1, 2 })
            _db.Execute(_db.Paths.ServerDatabasePath, $"INSERT INTO Posts (Id, CreatedAt, UpdatedAt, Md5, FileExt, IsDeleted) VALUES ({id}, '2026-01-01', '2026-01-01', '{new string('a', 32)}', 'png', 1);");

        // The deleted ones wait while a normal post is pending, and are handed out once it is not.
        Assert.Equal([3L], _store.NextPending(10, Now));
        _store.MarkSaved(Post(3), LocalPostStatus.Downloaded);
        Assert.Equal([1L, 2L], _store.NextPending(10, Now).Order().ToArray());
    }

    [Fact]
    public void A_post_waiting_out_a_retry_is_neither_next_nor_counted()
    {
        _store.RecordFailure(2, "boom", TimeSpan.FromHours(1));

        Assert.DoesNotContain(2L, _store.NextPending(10, DateTimeOffset.UtcNow));
        Assert.Equal(2, _store.CountPending(DateTimeOffset.UtcNow));
    }

    [Fact]
    public void A_post_comes_back_once_its_retry_time_has_passed()
    {
        _store.RecordFailure(2, "boom", TimeSpan.FromHours(1));
        var later = DateTimeOffset.UtcNow.AddHours(2);

        Assert.Contains(2L, _store.NextPending(10, later));
        Assert.Equal(3, _store.CountPending(later));
    }

    [Fact]
    public void Saving_clears_retry_bookkeeping_and_counts_today()
    {
        _store.RecordFailure(1, "boom", TimeSpan.FromHours(1));
        _store.MarkSaved(Post(1), LocalPostStatus.Downloaded);

        Assert.Empty(_store.GetProblems(10));
        Assert.Equal(1, _store.SavedToday());
    }

    [Fact]
    public void Saving_twice_is_idempotent()
    {
        _store.MarkSaved(Post(1), LocalPostStatus.Downloaded);
        _store.MarkSaved(Post(1), LocalPostStatus.Downloaded);

        Assert.Equal(1, _db.Scalar(_db.Paths.DeviceDatabasePath, "SELECT COUNT(*) FROM Posts WHERE Id = 1;"));
        Assert.Equal(1, _store.SavedToday());
    }

    [Fact]
    public void A_crash_after_the_device_write_still_counts_the_post_and_is_not_retried()
    {
        // The device row is written, the app-database step never ran: no log row, retry row still there.
        _store.RecordFailure(1, "boom", TimeSpan.FromMinutes(-5));
        _db.Execute(_db.Paths.DeviceDatabasePath, $"""
            INSERT INTO Posts (Id, CreatedAt, UpdatedAt, Md5, SavedAt, Status)
            VALUES (1, '2026-01-01', '2026-01-01', '{new string('a', 32)}', '{DateTimeOffset.UtcNow:o}', 1);
            """);

        Assert.Equal(1, _store.SavedToday());
        Assert.DoesNotContain(1L, _store.NextPending(10, DateTimeOffset.UtcNow));
        Assert.Equal(2, _store.CountPending(DateTimeOffset.UtcNow));

        _store.MarkSaved(Post(1), LocalPostStatus.Downloaded); // the redo after restart
        Assert.Equal(1, _store.SavedToday());
        Assert.Empty(_store.GetProblems(10));
    }

    [Fact]
    public void A_crash_before_the_device_write_leaves_the_post_pending_and_uncounted()
    {
        Assert.Equal(0, _store.SavedToday());
        Assert.Contains(1L, _store.NextPending(10, Now));
    }

    [Fact]
    public void Saving_again_keeps_the_original_SavedAt()
    {
        _db.Execute(_db.Paths.DeviceDatabasePath, $"""
            INSERT INTO Posts (Id, CreatedAt, UpdatedAt, Md5, SavedAt, Status)
            VALUES (1, '2026-01-01', '2026-01-01', '{new string('a', 32)}', '2020-05-05T00:00:00.0000000+00:00', 1);
            """);

        _store.MarkSaved(Post(1), LocalPostStatus.Downloaded);

        Assert.Equal(1, _db.Scalar(_db.Paths.DeviceDatabasePath,
            "SELECT COUNT(*) FROM Posts WHERE Id = 1 AND SavedAt = '2020-05-05T00:00:00.0000000+00:00';"));
        Assert.Equal(0, _store.SavedToday());
    }
}
