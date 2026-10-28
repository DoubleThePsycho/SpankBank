using Microsoft.Data.Sqlite;
using SpankBank.Data.RubbishBin;

namespace SpankBank.Data.Repositories;

/// <param name="Total">Posts on this device that the artist's watch matches (favorites included).</param>
/// <param name="Exclusive">Of those, the ones no other enabled watch matches (favorites excluded).</param>
/// <param name="Removable">All of them except favorites.</param>
/// <param name="Favorites">Favorited posts among them; these are never removed.</param>
public sealed record ArtistRemovalCounts(int Total, int Exclusive, int Removable, int Favorites);

/// <summary>What happens to an artist's downloaded posts when the artist is removed. Posts go to the rubbish bin.</summary>
public sealed class ArtistPostRemoval
{
    private readonly DatabasePaths _paths;
    private readonly BinStore _bin;

    public ArtistPostRemoval(DatabasePaths paths, BinStore bin)
    {
        _paths = paths;
        _bin = bin;
    }

    public ArtistRemovalCounts Counts(long watchId)
    {
        var (all, exclusive, favorites) = Load(watchId);
        return new ArtistRemovalCounts(all.Count + favorites, exclusive.Count, all.Count, favorites);
    }

    /// <returns>How many posts were sent to the bin.</returns>
    public int Remove(long watchId, bool exclusiveOnly)
    {
        var (all, exclusive, _) = Load(watchId);
        var removed = 0;
        foreach (var id in exclusiveOnly ? exclusive : all)
            if (_bin.RequestDelete(id, reason: "artist")) removed++;
        return removed;
    }

    private (List<long> All, List<long> Exclusive, int Favorites) Load(long watchId)
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
            SELECT m.PostId,
                   EXISTS (SELECT 1 FROM Favorites f WHERE f.PostId = m.PostId),
                   EXISTS (SELECT 1 FROM WatchMatches m2 JOIN WatchedItems w2 ON w2.Id = m2.WatchedItemId AND w2.Enabled = 1
                           WHERE m2.PostId = m.PostId AND m2.WatchedItemId <> $watch)
            FROM WatchMatches m
            JOIN dev.Posts d ON d.Id = m.PostId AND d.SavedAt IS NOT NULL
            WHERE m.WatchedItemId = $watch
              AND NOT EXISTS (SELECT 1 FROM DeletedPosts x WHERE x.PostId = m.PostId);
            """;
        cmd.Parameters.AddWithValue("$watch", watchId);

        var all = new List<long>();
        var exclusive = new List<long>();
        var favorites = 0;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (reader.GetInt32(1) != 0) { favorites++; continue; }
            all.Add(reader.GetInt64(0));
            if (reader.GetInt32(2) == 0) exclusive.Add(reader.GetInt64(0));
        }
        return (all, exclusive, favorites);
    }
}
