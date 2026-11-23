using Microsoft.Data.Sqlite;
using SpankBank.Data.Repositories;

namespace SpankBank.Data.Downloads;

/// <summary>
/// Keeps the tags stored with the posts on this device (device.sqlite) in step with the catalog's. Searching and the local tag counts
/// read only the local copy, so they never have to look every post up in the (huge) catalog; e621 changes tags over time (implications,
/// renamed artist tags), so after each export import (and at worker start) the posts whose catalog tags differ are updated.
/// </summary>
public sealed class LocalTagSync
{
    private readonly DatabasePaths _paths;

    public LocalTagSync(DatabasePaths paths) => _paths = paths;

    /// <summary>Copies the catalog's tags onto the local posts that differ; returns how many posts changed.</summary>
    public int Run()
    {
        int changed;
        using (var connection = new SqliteConnection($"Data Source={_paths.DeviceDatabasePath};Pooling=False"))
        {
            connection.Open();
            using (var attach = connection.CreateCommand())
            {
                attach.CommandText = $"ATTACH DATABASE '{_paths.ServerDatabasePath.Replace("'", "''")}' AS srv;";
                attach.ExecuteNonQuery();
            }
            using var cmd = connection.CreateCommand();
            cmd.CommandTimeout = 600;
            cmd.CommandText = """
                UPDATE Posts SET TagString = s.TagString
                FROM srv.Posts AS s
                WHERE s.Id = Posts.Id AND s.TagString IS NOT NULL AND Posts.TagString IS NOT s.TagString;
                """;
            changed = cmd.ExecuteNonQuery();
        }

        // The web process rebuilds its local tag counts when this changes.
        if (changed > 0)
            new SettingsRepository(_paths.AppDatabasePath).Set(WorkerSettingsKeys.LocalTagsSyncedAt, DateTimeOffset.UtcNow.ToString("o"));
        return changed;
    }
}
