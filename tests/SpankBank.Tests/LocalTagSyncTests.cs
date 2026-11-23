using Microsoft.Data.Sqlite;
using SpankBank.Data;
using SpankBank.Data.Downloads;
using SpankBank.Data.Repositories;

namespace SpankBank.Tests;

public class LocalTagSyncTests : IDisposable
{
    private readonly TempDatabases _db = new();

    public void Dispose() => _db.Dispose();

    private void Post(int id, string? catalogTags, string? localTags)
    {
        if (catalogTags is not null)
            _db.Execute(_db.Paths.ServerDatabasePath, $"INSERT INTO Posts (Id, CreatedAt, UpdatedAt, Md5, FileExt, TagString) VALUES ({id}, '2024-01-01', '2024-01-01', '{id:D32}', 'png', '{catalogTags}');");
        _db.Execute(_db.Paths.DeviceDatabasePath, $"INSERT INTO Posts (Id, CreatedAt, UpdatedAt, Md5, FileExt, TagString, SavedAt, Status) VALUES ({id}, '2024-01-01', '2024-01-01', '{id:D32}', 'png', {(localTags is null ? "NULL" : $"'{localTags}'")}, '2026-01-01T00:00:00+00:00', 1);");
    }

    private string? LocalTags(int id)
    {
        using var connection = new SqliteConnection($"Data Source={_db.Paths.DeviceDatabasePath};Pooling=False");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT TagString FROM Posts WHERE Id = {id};";
        return cmd.ExecuteScalar() as string;
    }

    [Fact]
    public void Copies_changed_catalog_tags_onto_local_posts_and_leaves_the_rest()
    {
        Post(1, "fox perissodactyl", "fox carnivoran"); // implications changed
        Post(2, "wolf", "wolf");                         // same
        Post(3, "otter", null);                          // never had a local copy
        Post(4, null, "only_local");                     // not in the catalog

        Assert.Equal(2, new LocalTagSync(_db.Paths).Run());

        Assert.Equal("fox perissodactyl", LocalTags(1));
        Assert.Equal("wolf", LocalTags(2));
        Assert.Equal("otter", LocalTags(3));
        Assert.Equal("only_local", LocalTags(4));
        Assert.Equal(0, new LocalTagSync(_db.Paths).Run());
    }

    [Fact]
    public void Records_when_tags_changed_so_the_web_rebuilds_its_counts()
    {
        var settings = new SettingsRepository(_db.Paths.AppDatabasePath);
        Post(1, "fox", "fox");
        new LocalTagSync(_db.Paths).Run();
        Assert.Null(settings.Get(WorkerSettingsKeys.LocalTagsSyncedAt));

        _db.Execute(_db.Paths.ServerDatabasePath, "UPDATE Posts SET TagString = 'fox male' WHERE Id = 1;");
        new LocalTagSync(_db.Paths).Run();
        Assert.NotNull(settings.Get(WorkerSettingsKeys.LocalTagsSyncedAt));
    }

    [Fact]
    public void Search_matches_the_local_copy_and_keeps_order_and_total()
    {
        Post(1, "fox", "fox");
        Post(2, "fox wolf", "fox wolf");
        Post(3, "wolf", "wolf");
        var search = new DownloadedPostSearch(_db.Paths);

        var page = search.Search("fox", 1, 10);
        Assert.Equal(2, page.Total);
        Assert.Equal([2L, 1L], page.Items.Select(i => i.Id).ToArray());

        var second = search.Search("fox limit:1", 2, 10);
        Assert.Equal(2, second.Total);
        Assert.Equal([1L], second.Items.Select(i => i.Id).ToArray());
    }
}
