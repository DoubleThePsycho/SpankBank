using SpankBank.Data.Downloads;
using SpankBank.Data.Repositories;
using SpankBank.Data.RubbishBin;
using SpankBank.Core.Models;

namespace SpankBank.Tests;

public class BinTests : IDisposable
{
    private static readonly string Md5 = new('a', 32);
    private const string SavedAt = "2026-01-01T00:00:00.0000000+00:00";

    private readonly TempDatabases _db = new();
    private readonly string _media = Path.Combine(Path.GetTempPath(), "spankbank-media-" + Guid.NewGuid().ToString("N"));
    private readonly BinStore _bin;

    public BinTests()
    {
        _bin = new BinStore(_db.Paths);
        _db.Execute(_db.Paths.DeviceDatabasePath, $"""
            INSERT INTO Posts (Id, CreatedAt, UpdatedAt, Md5, FileExt, SavedAt, Status) VALUES (1, '2024-01-01', '2024-01-01', '{Md5}', 'png', '{SavedAt}', 1);
            """);
        foreach (var file in Files()) { Directory.CreateDirectory(Path.GetDirectoryName(file)!); File.WriteAllText(file, "x"); }
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_media, recursive: true); } catch (IOException) { }
    }

    private string[] Files() => [MediaPaths.Original(_media, Md5, "png"), MediaPaths.Preview(_media, Md5), MediaPaths.Sample(_media, Md5)];
    private string[] BinFiles() => Files().Select(f => Path.Combine(_media, BinStore.BinFolder, Path.GetRelativePath(_media, f))).ToArray();
    private string? SavedAtOnDevice()
    {
        using var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_db.Paths.DeviceDatabasePath};Pooling=False");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT SavedAt FROM Posts WHERE Id = 1;";
        return cmd.ExecuteScalar() as string;
    }
    private long Count(string sql) => _db.Scalar(_db.Paths.AppDatabasePath, sql);

    [Fact]
    public void Delete_moves_the_files_to_the_bin_and_hides_the_post()
    {
        Assert.True(_bin.RequestDelete(1));
        _bin.Process(_media, DateTimeOffset.UtcNow);

        Assert.All(Files(), f => Assert.False(File.Exists(f)));
        Assert.All(BinFiles(), f => Assert.True(File.Exists(f)));
        Assert.Null(SavedAtOnDevice());
        Assert.Equal([1L], _bin.List().Select(p => p.PostId));
    }

    [Fact]
    public void Only_local_posts_can_be_deleted_and_only_once()
    {
        Assert.False(_bin.RequestDelete(99));
        Assert.True(_bin.RequestDelete(1));
        Assert.False(_bin.RequestDelete(1));
    }

    [Fact]
    public void Restore_brings_back_the_files_and_the_original_SavedAt()
    {
        _bin.RequestDelete(1);
        _bin.Process(_media, DateTimeOffset.UtcNow);
        Assert.True(_bin.RequestRestore(1));
        _bin.Process(_media, DateTimeOffset.UtcNow);

        Assert.All(Files(), f => Assert.True(File.Exists(f)));
        Assert.All(BinFiles(), f => Assert.False(File.Exists(f)));
        Assert.Equal(SavedAt, SavedAtOnDevice());
        Assert.Empty(_bin.List());
        Assert.Equal(0, Count("SELECT COUNT(*) FROM DeletedPosts;"));
    }

    [Fact]
    public void A_post_is_purged_after_thirty_days_but_stays_remembered()
    {
        _bin.RequestDelete(1);
        _bin.Process(_media, DateTimeOffset.UtcNow);

        _bin.Process(_media, DateTimeOffset.UtcNow.AddDays(29));
        Assert.All(BinFiles(), f => Assert.True(File.Exists(f)));

        _bin.Process(_media, DateTimeOffset.UtcNow.AddDays(31));
        Assert.All(BinFiles(), f => Assert.False(File.Exists(f)));
        Assert.Equal(1, Count("SELECT COUNT(*) FROM DeletedPosts WHERE PostId = 1 AND State = 3;"));
        Assert.Empty(_bin.List());
        Assert.Equal(0, _db.Scalar(_db.Paths.DeviceDatabasePath, "SELECT COUNT(*) FROM Posts WHERE Id = 1;"));
    }

    [Fact]
    public void Empty_bin_purges_everything_now_even_posts_not_moved_yet()
    {
        _bin.RequestDelete(1);
        _bin.RequestEmpty(); // before the worker moved anything
        _bin.Process(_media, DateTimeOffset.UtcNow);

        Assert.All(Files(), f => Assert.False(File.Exists(f)));
        Assert.All(BinFiles(), f => Assert.False(File.Exists(f)));
        Assert.Empty(_bin.List());
    }

    [Fact]
    public void A_restore_is_not_possible_after_the_purge()
    {
        _bin.RequestDelete(1);
        _bin.Process(_media, DateTimeOffset.UtcNow);
        _bin.RequestEmpty();
        _bin.Process(_media, DateTimeOffset.UtcNow);

        Assert.False(_bin.RequestRestore(1));
    }

    [Fact]
    public void Processing_is_idempotent()
    {
        _bin.RequestDelete(1);
        Assert.Equal(1, _bin.Process(_media, DateTimeOffset.UtcNow));
        Assert.Equal(0, _bin.Process(_media, DateTimeOffset.UtcNow));
        Assert.All(BinFiles(), f => Assert.True(File.Exists(f)));
    }

    [Fact]
    public void Deleted_posts_are_never_queued_again_not_even_after_the_purge()
    {
        var watch = new WatchesRepository(_db.Paths.AppDatabasePath, _db.Paths.ServerDatabasePath).Add(WatchedItemType.Search, "w", "w")!.Value;
        _db.Execute(_db.Paths.AppDatabasePath, $"INSERT INTO WatchMatches (WatchedItemId, PostId) VALUES ({watch}, 1);");
        var store = new DownloadStore(_db.Paths);

        _bin.RequestDelete(1);
        _bin.Process(_media, DateTimeOffset.UtcNow);
        Assert.DoesNotContain(1L, store.NextPending(10, DateTimeOffset.UtcNow));
        Assert.Equal(0, store.CountPending(DateTimeOffset.UtcNow));

        _bin.Process(_media, DateTimeOffset.UtcNow.AddDays(40)); // purged
        Assert.DoesNotContain(1L, store.NextPending(10, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void A_post_waiting_for_the_worker_is_already_hidden_from_listings()
    {
        _bin.RequestDelete(1);
        var search = new DownloadedPostSearch(_db.Paths);
        Assert.Empty(search.Search("", 1, 50).Items);
    }
}
