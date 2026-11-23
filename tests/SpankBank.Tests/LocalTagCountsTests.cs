using SpankBank.Data.Repositories;

namespace SpankBank.Tests;

public class LocalTagCountsTests : IDisposable
{
    private readonly TempDatabases _db = new();

    public void Dispose() => _db.Dispose();

    private void Post(int id, string tags, string? savedAt, string? deviceTags = null)
    {
        _db.Execute(_db.Paths.ServerDatabasePath, $"INSERT OR REPLACE INTO Posts (Id, CreatedAt, UpdatedAt, Md5, FileExt, TagString) VALUES ({id}, '2024-01-01', '2024-01-01', '{id:D32}', 'png', '{tags}');");
        _db.Execute(_db.Paths.DeviceDatabasePath, $"INSERT OR REPLACE INTO Posts (Id, CreatedAt, UpdatedAt, Md5, FileExt, TagString, SavedAt, Status) VALUES ({id}, '2024-01-01', '2024-01-01', '{id:D32}', 'png', '{deviceTags ?? tags}', {(savedAt is null ? "NULL" : $"'{savedAt}'")}, 1);");
    }

    [Fact]
    public void Counts_the_tags_of_posts_saved_on_this_device_only()
    {
        Post(1, "fox male", "2026-01-01T00:00:00+00:00");
        Post(2, "fox female", "2026-01-02T00:00:00+00:00");
        Post(3, "fox wolf", null); // in the catalog, never downloaded
        var counts = new LocalTagCounts(_db.Paths);

        Assert.Equal(2, counts.Get("fox"));
        Assert.Equal(1, counts.Get("male"));
        Assert.Equal(0, counts.Get("wolf"));
        Assert.Equal(0, counts.Get("unknown"));
    }

    [Fact]
    public void Counts_the_local_copy_of_the_tags_until_the_tag_sync_updates_it()
    {
        Post(1, "fox implied_tag", "2026-01-01T00:00:00+00:00", deviceTags: "fox");
        var counts = new LocalTagCounts(_db.Paths, refreshAfter: TimeSpan.Zero);
        Assert.Equal(0, counts.Get("implied_tag"));

        Assert.Equal(1, new SpankBank.Data.Downloads.LocalTagSync(_db.Paths).Run());
        Wait(() => counts.Get("implied_tag") == 1);
    }

    [Fact]
    public void Posts_saved_later_are_counted_in_and_removed_ones_drop_out_after_the_refresh_interval()
    {
        Post(1, "fox", "2026-01-01T00:00:00+00:00");
        var counts = new LocalTagCounts(_db.Paths, refreshAfter: TimeSpan.Zero);
        Assert.Equal(1, counts.Get("fox"));

        Post(2, "fox", "2026-01-02T00:00:00+00:00");
        Wait(() => counts.Get("fox") == 2);

        _db.Execute(_db.Paths.DeviceDatabasePath, "UPDATE Posts SET SavedAt = NULL WHERE Id = 1;");
        Wait(() => counts.Get("fox") == 1);
    }

    private static void Wait(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++) Thread.Sleep(50);
        Assert.True(condition());
    }
}
