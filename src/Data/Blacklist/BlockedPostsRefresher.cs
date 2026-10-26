using Microsoft.Data.Sqlite;
using SpankBank.Core.Search;

namespace SpankBank.Data.Blacklist;

/// <summary>
/// Works out which posts the blacklist blocks: the posts enabled watches match and the posts already on this device.
/// Posts matched by a watch that ignores the blacklist are never blocked. Idempotent; the worker runs it when the
/// blacklist, a watch's override or the matches changed.
/// </summary>
public sealed class BlockedPostsRefresher
{
    private const int Chunk = 500;

    private readonly DatabasePaths _paths;

    public BlockedPostsRefresher(DatabasePaths paths) => _paths = paths;

    /// <returns>How many posts are blocked now.</returns>
    public int Refresh()
    {
        using var app = new SqliteConnection($"Data Source={_paths.AppDatabasePath};Pooling=False");
        app.Open();
        using (var attach = app.CreateCommand())
        {
            attach.CommandText = $"ATTACH DATABASE '{_paths.DeviceDatabasePath.Replace("'", "''")}' AS dev;";
            attach.ExecuteNonQuery();
        }

        var entries = new List<string>();
        using (var cmd = app.CreateCommand())
        {
            cmd.CommandText = "SELECT Query FROM Blacklist ORDER BY Id;";
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) entries.Add(reader.GetString(0));
        }
        var rules = BlacklistRules.Create(entries);

        var blocked = new List<long>();
        if (rules.Count > 0)
        {
            var candidates = Candidates(app);
            using var server = new SqliteConnection($"Data Source={_paths.ServerDatabasePath};Mode=ReadOnly");
            server.Open();
            foreach (var chunk in candidates.Chunk(Chunk))
                blocked.AddRange(Evaluate(server, rules, chunk));
        }

        using var transaction = app.BeginTransaction();
        using (var clear = app.CreateCommand())
        {
            clear.Transaction = transaction;
            clear.CommandText = "DELETE FROM BlockedPosts;";
            clear.ExecuteNonQuery();
        }
        using (var insert = app.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = "INSERT OR IGNORE INTO BlockedPosts (PostId) VALUES ($id);";
            var id = insert.Parameters.Add("$id", SqliteType.Integer);
            foreach (var postId in blocked)
            {
                id.Value = postId;
                insert.ExecuteNonQuery();
            }
        }
        transaction.Commit();
        return blocked.Count;
    }

    private static HashSet<long> Candidates(SqliteConnection app)
    {
        var ids = new HashSet<long>();
        void Load(string sql)
        {
            using var cmd = app.CreateCommand();
            cmd.CommandText = sql;
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) ids.Add(reader.GetInt64(0));
        }

        Load("""
            SELECT DISTINCT m.PostId FROM WatchMatches m
            JOIN WatchedItems w ON w.Id = m.WatchedItemId AND w.Enabled = 1;
            """);
        Load("SELECT Id FROM dev.Posts WHERE SavedAt IS NOT NULL;");

        var exempt = new List<long>();
        using (var cmd = app.CreateCommand())
        {
            cmd.CommandText = """
                SELECT DISTINCT m.PostId FROM WatchMatches m
                JOIN WatchedItems w ON w.Id = m.WatchedItemId AND w.Enabled = 1 AND w.IgnoreBlacklist = 1;
                """;
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) exempt.Add(reader.GetInt64(0));
        }
        ids.ExceptWith(exempt);
        return ids;
    }

    private static List<long> Evaluate(SqliteConnection server, BlacklistRules rules, long[] ids)
    {
        var blocked = new List<long>();
        using var cmd = server.CreateCommand();
        var placeholders = ids.Select((_, i) => $"$i{i}").ToList();
        cmd.CommandText = $"""
            SELECT Id, TagString, Rating, Score, FavCount, FileExt, ImageWidth, ImageHeight,
                   ParentId, FileSize, IsDeleted, CommentCount, CreatedAt, Md5
            FROM Posts WHERE Id IN ({string.Join(", ", placeholders)});
            """;
        for (var i = 0; i < ids.Length; i++) cmd.Parameters.AddWithValue(placeholders[i], ids[i]);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var tagString = reader.IsDBNull(1) ? "" : reader.GetString(1);
            var tagCount = tagString.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
            var row = new PostMatchRow(
                Id: reader.GetInt64(0),
                Rating: reader.GetInt32(2),
                Score: reader.GetInt32(3),
                FavCount: reader.GetInt32(4),
                FileExt: reader.IsDBNull(5) ? null : reader.GetString(5),
                Width: reader.IsDBNull(6) ? 0 : reader.GetInt32(6),
                Height: reader.IsDBNull(7) ? 0 : reader.GetInt32(7),
                ParentId: reader.IsDBNull(8) ? null : reader.GetInt64(8),
                FileSize: reader.IsDBNull(9) ? 0 : reader.GetInt64(9),
                IsDeleted: reader.GetInt32(10) != 0,
                CommentCount: reader.GetInt32(11),
                CreatedAt: reader.GetString(12),
                Md5: reader.IsDBNull(13) ? null : reader.GetString(13),
                TagCount: tagCount);
            if (rules.Matches(tagString, row)) blocked.Add(row.Id);
        }
        return blocked;
    }
}
