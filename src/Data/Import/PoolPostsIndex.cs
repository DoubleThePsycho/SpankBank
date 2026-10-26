using Microsoft.Data.Sqlite;

namespace SpankBank.Data.Import;

/// <summary>Keeps the PoolPosts table (post to pool lookup) in step with the pools' post lists.</summary>
public static class PoolPostsIndex
{
    /// <summary>Rebuilds PoolPosts from Pools.PostIds inside the given transaction.</summary>
    public static long Rebuild(SqliteConnection connection, SqliteTransaction transaction)
    {
        using (var clear = connection.CreateCommand())
        {
            clear.Transaction = transaction;
            clear.CommandText = "DELETE FROM PoolPosts;";
            clear.ExecuteNonQuery();
        }

        var pools = new List<(long Id, string PostIds)>();
        using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = "SELECT Id, PostIds FROM Pools;";
            using var reader = read.ExecuteReader();
            while (reader.Read()) pools.Add((reader.GetInt64(0), reader.GetString(1)));
        }

        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = "INSERT OR IGNORE INTO PoolPosts (PoolId, PostId, Position) VALUES ($pool, $post, $position);";
        var pool = insert.Parameters.Add("$pool", SqliteType.Integer);
        var post = insert.Parameters.Add("$post", SqliteType.Integer);
        var position = insert.Parameters.Add("$position", SqliteType.Integer);
        insert.Prepare();

        long rows = 0;
        foreach (var (id, postIds) in pools)
        {
            pool.Value = id;
            var index = 0;
            foreach (var part in postIds.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                index++;
                if (!long.TryParse(part, out var postId)) continue;
                post.Value = postId;
                position.Value = index;
                insert.ExecuteNonQuery();
                rows++;
            }
        }
        return rows;
    }

    /// <summary>Fills PoolPosts when pools were imported before the table existed.</summary>
    public static void EnsureFilled(string serverDatabasePath)
    {
        using var connection = new SqliteConnection($"Data Source={serverDatabasePath}");
        connection.Open();
        using (var check = connection.CreateCommand())
        {
            check.CommandText = "SELECT (SELECT COUNT(*) FROM PoolPosts), (SELECT COUNT(*) FROM Pools);";
            using var reader = check.ExecuteReader();
            reader.Read();
            if (reader.GetInt64(0) > 0 || reader.GetInt64(1) == 0) return;
        }

        using var transaction = connection.BeginTransaction();
        Rebuild(connection, transaction);
        transaction.Commit();
    }
}
