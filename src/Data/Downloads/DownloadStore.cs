using Microsoft.Data.Sqlite;
using SpankBank.Core.Models;

namespace SpankBank.Data.Downloads;

/// <summary>The download queue: which matched posts still need a file, and the bookkeeping around fetching them.</summary>
public sealed class DownloadStore
{
    private readonly DatabasePaths _paths;

    public DownloadStore(DatabasePaths paths) => _paths = paths;

    /// <summary>
    /// Matched posts of enabled watches that are neither on this device, blacklisted nor waiting out a retry delay.
    /// Posts the export does not mark deleted come first. The others have to be looked for at their sources and in the
    /// Wayback Machine, which is slow and rate-limited, so they only get a few places per batch and never hold up the files e621 serves.
    /// </summary>
    /// <param name="includeDeleted">False while the Wayback Machine is off limits: deleted posts can only come from there (or their sources).</param>
    public IReadOnlyList<long> NextPending(int limit, DateTimeOffset now, bool includeDeleted = true)
    {
        using var connection = OpenAttached();
        var reserved = limit >= 6 ? limit / 6 : 0;
        var ids = QueryPending(connection, limit - reserved, now, PendingKind.NotDeleted);
        // Room the normal posts did not fill goes to the deleted ones too.
        if (includeDeleted) ids.AddRange(QueryPending(connection, limit - ids.Count, now, PendingKind.Deleted));
        return ids;
    }

    private enum PendingKind { NotDeleted, Deleted }

    private static List<long> QueryPending(SqliteConnection connection, int limit, DateTimeOffset now, PendingKind kind)
    {
        if (limit <= 0) return [];

        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT DISTINCT m.PostId
            FROM WatchMatches m
            JOIN WatchedItems w ON w.Id = m.WatchedItemId AND w.Enabled = 1
            LEFT JOIN srv.Posts p ON p.Id = m.PostId
            WHERE NOT EXISTS (SELECT 1 FROM dev.Posts d WHERE d.Id = m.PostId AND d.SavedAt IS NOT NULL)
              AND NOT EXISTS (SELECT 1 FROM DownloadAttempts a WHERE a.PostId = m.PostId AND a.NextAttemptAt > $now)
              AND NOT EXISTS (SELECT 1 FROM BlockedPosts b WHERE b.PostId = m.PostId)
              AND NOT EXISTS (SELECT 1 FROM DeletedPosts x WHERE x.PostId = m.PostId)
              AND COALESCE(p.IsDeleted, 0) {(kind == PendingKind.Deleted ? "= 1" : "= 0")}
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$now", now.ToString("o"));
        cmd.Parameters.AddWithValue("$limit", limit);
        using var reader = cmd.ExecuteReader();
        var ids = new List<long>();
        while (reader.Read()) ids.Add(reader.GetInt64(0));
        return ids;
    }

    /// <summary>
    /// Posts marked lost before sources were tried are looked at again when a source address exists for them (they would
    /// otherwise wait out the 30-day retry).
    /// </summary>
    /// <returns>How many posts will be tried again.</returns>
    public int RetryLostWithSources(DateTimeOffset now)
    {
        using var connection = OpenAttached();
        var retry = new List<long>();
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT a.PostId, p.Source
                FROM DownloadAttempts a JOIN srv.Posts p ON p.Id = a.PostId
                WHERE a.LastError LIKE 'Deleted on % and not found%' AND a.NextAttemptAt > $now AND p.Source IS NOT NULL;
                """;
            cmd.Parameters.AddWithValue("$now", now.ToString("o"));
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var source = reader.GetString(1);
                if (SourceLinks.DirectFileUrls(source).Count > 0 || SourceLinks.InkbunnySubmissionIds(source).Count > 0) retry.Add(reader.GetInt64(0));
            }
        }

        using var transaction = connection.BeginTransaction();
        foreach (var id in retry)
        {
            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = "UPDATE DownloadAttempts SET NextAttemptAt = $now WHERE PostId = $id;";
            update.Parameters.AddWithValue("$now", now.ToString("o"));
            update.Parameters.AddWithValue("$id", id);
            update.ExecuteNonQuery();
        }
        transaction.Commit();
        return retry.Count;
    }

    public int CountPending(DateTimeOffset now)
    {
        using var connection = OpenAttached();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(DISTINCT m.PostId)
            FROM WatchMatches m
            JOIN WatchedItems w ON w.Id = m.WatchedItemId AND w.Enabled = 1
            WHERE NOT EXISTS (SELECT 1 FROM dev.Posts d WHERE d.Id = m.PostId AND d.SavedAt IS NOT NULL)
              AND NOT EXISTS (SELECT 1 FROM DownloadAttempts a WHERE a.PostId = m.PostId AND a.NextAttemptAt > $now)
              AND NOT EXISTS (SELECT 1 FROM BlockedPosts b WHERE b.PostId = m.PostId)
              AND NOT EXISTS (SELECT 1 FROM DeletedPosts x WHERE x.PostId = m.PostId);
            """;
        cmd.Parameters.AddWithValue("$now", now.ToString("o"));
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public CatalogPost? GetCatalogPost(long id)
    {
        using var connection = new SqliteConnection($"Data Source={_paths.ServerDatabasePath};Mode=ReadOnly");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT Id, Md5, FileExt, IsDeleted, Rating, Score, ImageWidth, ImageHeight, FileSize,
                   CreatedAt, UpdatedAt, TagString, Description, Duration, ParentId, Source
            FROM Posts WHERE Id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", id);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;

        return new CatalogPost(
            reader.GetInt64(0),
            reader.IsDBNull(1) ? "" : reader.GetString(1),
            reader.IsDBNull(2) ? "" : reader.GetString(2),
            reader.GetInt32(3) != 0,
            reader.GetInt32(4),
            reader.GetInt32(5),
            reader.IsDBNull(6) ? null : reader.GetInt32(6),
            reader.IsDBNull(7) ? null : reader.GetInt32(7),
            reader.IsDBNull(8) ? null : reader.GetInt64(8),
            reader.GetString(9),
            reader.GetString(10),
            reader.IsDBNull(11) ? "" : reader.GetString(11),
            reader.IsDBNull(12) ? null : reader.GetString(12),
            reader.IsDBNull(13) ? null : reader.GetDouble(13),
            reader.IsDBNull(14) ? null : reader.GetInt64(14),
            reader.IsDBNull(15) ? null : reader.GetString(15));
    }

    /// <summary>
    /// Records that the file is on disk: the post enters device.sqlite (the source of truth, also for the daily count)
    /// and any retry bookkeeping is cleared. Saving again keeps the original SavedAt. A crash between the two steps is harmless: the post counts as saved, and
    /// the leftover retry row is ignored because saved posts are never pending.
    /// </summary>
    public void MarkSaved(CatalogPost post, LocalPostStatus status)
    {
        var now = DateTimeOffset.UtcNow.ToString("o");

        using (var device = new SqliteConnection($"Data Source={_paths.DeviceDatabasePath}"))
        {
            device.Open();
            using var cmd = device.CreateCommand();
            cmd.CommandText = """
                INSERT OR REPLACE INTO Posts
                    (Id, CreatedAt, UpdatedAt, Md5, Rating, Score, ImageWidth, ImageHeight, FileSize, FileExt,
                     TagString, Description, Duration, ParentId, SavedAt, Status)
                VALUES
                    ($id, $created, $updated, $md5, $rating, $score, $width, $height, $size, $ext,
                     $tags, $description, $duration, $parent,
                     COALESCE((SELECT SavedAt FROM Posts WHERE Id = $id), $saved), $status);
                """;
            cmd.Parameters.AddWithValue("$id", post.Id);
            cmd.Parameters.AddWithValue("$created", post.CreatedAt);
            cmd.Parameters.AddWithValue("$updated", post.UpdatedAt);
            cmd.Parameters.AddWithValue("$md5", post.Md5);
            cmd.Parameters.AddWithValue("$rating", post.Rating);
            cmd.Parameters.AddWithValue("$score", post.Score);
            cmd.Parameters.AddWithValue("$width", (object?)post.Width ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$height", (object?)post.Height ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$size", (object?)post.FileSize ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$ext", post.Ext);
            cmd.Parameters.AddWithValue("$tags", post.TagString);
            cmd.Parameters.AddWithValue("$description", (object?)post.Description ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$duration", (object?)post.Duration ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$parent", (object?)post.ParentId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$saved", now);
            cmd.Parameters.AddWithValue("$status", (int)status);
            cmd.ExecuteNonQuery();
        }

        using var app = new SqliteConnection($"Data Source={_paths.AppDatabasePath}");
        app.Open();
        using var transaction = app.BeginTransaction();
        using (var log = app.CreateCommand())
        {
            log.Transaction = transaction;
            log.CommandText = "INSERT INTO DownloadLog (PostId, At, Status) VALUES ($id, $at, $status);";
            log.Parameters.AddWithValue("$id", post.Id);
            log.Parameters.AddWithValue("$at", now);
            log.Parameters.AddWithValue("$status", (int)status);
            log.ExecuteNonQuery();
        }
        using (var clear = app.CreateCommand())
        {
            clear.Transaction = transaction;
            clear.CommandText = "DELETE FROM DownloadAttempts WHERE PostId = $id;";
            clear.Parameters.AddWithValue("$id", post.Id);
            clear.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    /// <summary>Puts a post off until <paramref name="until"/> without counting an attempt (the archive asked us to slow down).</summary>
    public void Defer(long postId, DateTimeOffset until, string reason)
    {
        var now = DateTimeOffset.UtcNow;
        using var connection = new SqliteConnection($"Data Source={_paths.AppDatabasePath}");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO DownloadAttempts (PostId, Attempts, LastAttemptAt, NextAttemptAt, LastError)
            VALUES ($id, 0, $now, $next, $error)
            ON CONFLICT(PostId) DO UPDATE SET LastAttemptAt = $now, NextAttemptAt = $next, LastError = $error;
            """;
        cmd.Parameters.AddWithValue("$id", postId);
        cmd.Parameters.AddWithValue("$now", now.ToString("o"));
        cmd.Parameters.AddWithValue("$next", until.ToString("o"));
        cmd.Parameters.AddWithValue("$error", reason);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Notes a failed attempt and when the post may be tried again.</summary>
    public void RecordFailure(long postId, string error, TimeSpan retryAfter)
    {
        var now = DateTimeOffset.UtcNow;
        using var connection = new SqliteConnection($"Data Source={_paths.AppDatabasePath}");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO DownloadAttempts (PostId, Attempts, LastAttemptAt, NextAttemptAt, LastError)
            VALUES ($id, 1, $now, $next, $error)
            ON CONFLICT(PostId) DO UPDATE SET
                Attempts = Attempts + 1, LastAttemptAt = $now, NextAttemptAt = $next, LastError = $error;
            """;
        cmd.Parameters.AddWithValue("$id", postId);
        cmd.Parameters.AddWithValue("$now", now.ToString("o"));
        cmd.Parameters.AddWithValue("$next", (now + retryAfter).ToString("o"));
        cmd.Parameters.AddWithValue("$error", error);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Posts the downloader gave up on for now (or failed to fetch), newest attempt first.</summary>
    public IReadOnlyList<DownloadProblem> GetProblems(int limit)
    {
        using var connection = new SqliteConnection($"Data Source={_paths.AppDatabasePath}");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT PostId, Attempts, LastAttemptAt, NextAttemptAt, LastError
            FROM DownloadAttempts ORDER BY LastAttemptAt DESC LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$limit", limit);
        using var reader = cmd.ExecuteReader();
        var problems = new List<DownloadProblem>();
        while (reader.Read())
            problems.Add(new DownloadProblem(reader.GetInt64(0), reader.GetInt32(1), DateTimeOffset.Parse(reader.GetString(2)),
                DateTimeOffset.Parse(reader.GetString(3)), reader.IsDBNull(4) ? "" : reader.GetString(4)));
        return problems;
    }

    public int CountProblems()
    {
        using var connection = new SqliteConnection($"Data Source={_paths.AppDatabasePath}");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM DownloadAttempts;";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public int AttemptsSoFar(long postId)
    {
        using var connection = new SqliteConnection($"Data Source={_paths.AppDatabasePath}");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT Attempts FROM DownloadAttempts WHERE PostId = $id;";
        cmd.Parameters.AddWithValue("$id", postId);
        return cmd.ExecuteScalar() is long attempts ? (int)attempts : 0;
    }

    /// <summary>Files obtained since 00:00 UTC today. Counted from device.sqlite, the record of what is actually saved, so a crash cannot skew it.</summary>
    public int SavedToday()
    {
        var midnight = new DateTimeOffset(DateTimeOffset.UtcNow.Date, TimeSpan.Zero).ToString("o");
        using var connection = new SqliteConnection($"Data Source={_paths.DeviceDatabasePath}");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM Posts WHERE SavedAt >= $midnight;";
        cmd.Parameters.AddWithValue("$midnight", midnight);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    // The app database with device.sqlite attached as "dev". Not pooled: a pooled connection would keep the attachment.
    private SqliteConnection OpenAttached()
    {
        var connection = new SqliteConnection($"Data Source={_paths.AppDatabasePath};Pooling=False");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"ATTACH DATABASE '{_paths.DeviceDatabasePath.Replace("'", "''")}' AS dev;";
        cmd.ExecuteNonQuery();
        cmd.CommandText = $"ATTACH DATABASE '{_paths.ServerDatabasePath.Replace("'", "''")}' AS srv;";
        cmd.ExecuteNonQuery();
        return connection;
    }
}

public sealed record DownloadProblem(long PostId, int Attempts, DateTimeOffset LastAttemptAt, DateTimeOffset NextAttemptAt, string Error);
