using Microsoft.Data.Sqlite;
using SpankBank.Data.RubbishBin;

namespace SpankBank.Data.Repositories;

/// <summary>
/// Stray posts: on this device, but no enabled watch matches them and they are not favorites. Listed with the
/// search term stray:true; moving them to the bin is always the user's call, never automatic.
/// </summary>
public sealed class StrayPosts
{
    private readonly DatabasePaths _paths;
    private readonly BinStore _bin;

    public StrayPosts(DatabasePaths paths, BinStore bin)
    {
        _paths = paths;
        _bin = bin;
    }

    public int Count() => Ids().Count;

    /// <returns>How many posts were sent to the bin. They leave no tombstone: a watch that matches them later downloads them again.</returns>
    public int BinAll()
    {
        var binned = 0;
        foreach (var id in Ids())
            if (_bin.RequestDelete(id, reason: "stray")) binned++;
        return binned;
    }

    private List<long> Ids()
    {
        using var connection = new SqliteConnection($"Data Source={_paths.AppDatabasePath};Pooling=False");
        connection.Open();
        using (var attach = connection.CreateCommand())
        {
            attach.CommandText = $"ATTACH DATABASE '{_paths.DeviceDatabasePath.Replace("'", "''")}' AS dev;";
            attach.ExecuteNonQuery();
        }

        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT d.Id FROM dev.Posts d
            WHERE d.SavedAt IS NOT NULL
              AND NOT EXISTS (SELECT 1 FROM WatchMatches sm JOIN WatchedItems sw ON sw.Id = sm.WatchedItemId AND sw.Enabled = 1 WHERE sm.PostId = d.Id)
              AND NOT EXISTS (SELECT 1 FROM Favorites sf WHERE sf.PostId = d.Id)
              AND NOT EXISTS (SELECT 1 FROM DeletedPosts x WHERE x.PostId = d.Id);
            """;
        using var reader = cmd.ExecuteReader();
        var ids = new List<long>();
        while (reader.Read()) ids.Add(reader.GetInt64(0));
        return ids;
    }
}
