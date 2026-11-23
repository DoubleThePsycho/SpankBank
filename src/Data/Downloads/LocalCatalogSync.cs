using Microsoft.Data.Sqlite;
using SpankBank.Data.Repositories;

namespace SpankBank.Data.Downloads;

/// <summary>How many local posts <see cref="LocalCatalogSync"/> changed.</summary>
public sealed record LocalSyncResult(int Tags, int Counters);

/// <summary>
/// Keeps what searching needs from the catalog (server.sqlite, about 8 GB) copied onto the posts on this device (device.sqlite):
/// the tags, the favorite count and the deleted flag. Searching and the local tag counts read only that local copy, so they never have
/// to look every post up in the catalog (random reads that take many seconds when the catalog is not in the OS cache). e621 changes
/// these over time (implications, renamed artist tags, favorites, deletions), so after each export import and at worker start the posts
/// whose catalog values differ are updated.
/// </summary>
public sealed class LocalCatalogSync
{
    private readonly DatabasePaths _paths;

    public LocalCatalogSync(DatabasePaths paths) => _paths = paths;

    public LocalSyncResult Run()
    {
        int tags, counters;
        using (var connection = new SqliteConnection($"Data Source={_paths.DeviceDatabasePath};Pooling=False"))
        {
            connection.Open();
            using (var attach = connection.CreateCommand())
            {
                attach.CommandText = $"ATTACH DATABASE '{_paths.ServerDatabasePath.Replace("'", "''")}' AS srv;";
                attach.ExecuteNonQuery();
            }
            tags = Update(connection, """
                UPDATE Posts SET TagString = s.TagString
                FROM srv.Posts AS s
                WHERE s.Id = Posts.Id AND s.TagString IS NOT NULL AND Posts.TagString IS NOT s.TagString;
                """);
            counters = Update(connection, """
                UPDATE Posts SET FavCount = s.FavCount, IsDeleted = s.IsDeleted
                FROM srv.Posts AS s
                WHERE s.Id = Posts.Id AND (Posts.FavCount IS NOT s.FavCount OR Posts.IsDeleted IS NOT s.IsDeleted);
                """);
        }

        // The web process rebuilds its local tag counts when this changes.
        if (tags > 0)
            new SettingsRepository(_paths.AppDatabasePath).Set(WorkerSettingsKeys.LocalTagsSyncedAt, DateTimeOffset.UtcNow.ToString("o"));
        return new LocalSyncResult(tags, counters);
    }

    private static int Update(SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandTimeout = 600;
        cmd.CommandText = sql;
        return cmd.ExecuteNonQuery();
    }
}
