using Microsoft.Data.Sqlite;
using SpankBank.Core.Models;

namespace SpankBank.Data.Downloads;

/// <summary>The download queue: which matched posts still need a file, and the bookkeeping around fetching them.</summary>
public sealed class DownloadStore
{
    private readonly DatabasePaths _paths;

    public DownloadStore(DatabasePaths paths) => _paths = paths;

    /// <summary>Matched posts of enabled watches that are neither on this device nor waiting out a retry delay.</summary>
    public IReadOnlyList<long> NextPending(int limit, DateTimeOffset now)
    {
        using var connection = OpenAttached();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT DISTINCT m.PostId
            FROM WatchMatches m
            JOIN WatchedItems w ON w.Id = m.WatchedItemId AND w.Enabled = 1
            WHERE NOT EXISTS (SELECT 1 FROM dev.Posts d WHERE d.Id = m.PostId AND d.SavedAt IS NOT NULL)
              AND NOT EXISTS (SELECT 1 FROM DownloadAttempts a WHERE a.PostId = m.PostId AND a.NextAttemptAt > $now)
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$now", now.ToString("o"));
        cmd.Parameters.AddWithValue("$limit", limit);
        using var reader = cmd.ExecuteReader();
        var ids = new List<long>();
        while (reader.Read()) ids.Add(reader.GetInt64(0));
        return ids;
    }

    public int CountPending(DateTimeOffset now)
    {
        using var connection = OpenAttached();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(DISTINCT m.PostId)
            FROM WatchMatches m
            JOIN WatchedItems w ON w.Id = m.WatchedItemId AND w.Enabled = 1
            WHERE NOT EXISTS (SELECT 1 FROM dev.Posts d WHERE d.Id = m.PostId AND d.SavedAt IS NOT NULL);
            """;
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public CatalogPost? GetCatalogPost(long id)
    {
        using var connection = new SqliteConnection($"Data Source={_paths.ServerDatabasePath};Mode=ReadOnly");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT Id, Md5, FileExt, IsDeleted, Rating, Score, ImageWidth, ImageHeight, FileSize,
                   CreatedAt, UpdatedAt, TagString, Description, Duration, ParentId
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
            reader.IsDBNull(14) ? null : reader.GetInt64(14));
    }

    /// <summary>Records that the file is on disk: the post enters device.sqlite and any retry bookkeeping is cleared.</summary>
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
                     $tags, $description, $duration, $parent, $saved, $status);
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

    /// <summary>Files obtained since 00:00 UTC today.</summary>
    public int SavedToday()
    {
        var midnight = new DateTimeOffset(DateTimeOffset.UtcNow.Date, TimeSpan.Zero).ToString("o");
        using var connection = new SqliteConnection($"Data Source={_paths.AppDatabasePath}");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM DownloadLog WHERE At >= $midnight;";
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
        return connection;
    }
}

public sealed record DownloadProblem(long PostId, int Attempts, DateTimeOffset LastAttemptAt, DateTimeOffset NextAttemptAt, string Error);
