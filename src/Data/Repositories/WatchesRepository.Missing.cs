using Microsoft.Data.Sqlite;
using SpankBank.Core.Models;

namespace SpankBank.Data.Repositories;

/// <summary>A matched post that is not on this device: lost (deleted on the site, no archive has it) or pending (still to be fetched).</summary>
/// <param name="LastError">Why the last attempt failed (the lost reason), if any.</param>
/// <param name="QueuedImport">An uploaded file is waiting for the worker.</param>
/// <param name="ImportError">The worker could not use the uploaded file.</param>
public sealed record MissingPost(long Id, string Md5, string Ext, bool IsDeleted, int Rating, int Score, string CreatedAt, long? FileSize,
    int? Width, int? Height, string TagString, string? LastError, bool QueuedImport, string? ImportError);

public sealed partial class WatchesRepository
{
    public const string MissingLost = "lost";
    public const string MissingPending = "pending";

    // The same rules as GetPostStats: lost = a recorded "deleted and not found" failure; pending = not tried yet or waiting for a retry.
    private const string MissingFrom = $"""
        FROM WatchMatches m
        JOIN srv.Posts p ON p.Id = m.PostId
        LEFT JOIN dev.Posts d ON d.Id = m.PostId AND d.SavedAt IS NOT NULL
        LEFT JOIN DownloadAttempts x ON x.PostId = m.PostId AND {LostCondition}
        LEFT JOIN BlockedPosts b ON b.PostId = m.PostId
        LEFT JOIN DeletedPosts g ON g.PostId = m.PostId
        LEFT JOIN LocalImports i ON i.PostId = m.PostId
        LEFT JOIN DownloadAttempts a ON a.PostId = m.PostId
        """;

    private static string MissingWhere(string kind) => kind == MissingLost
        ? "m.WatchedItemId = $watch AND d.Id IS NULL AND x.PostId IS NOT NULL"
        : "m.WatchedItemId = $watch AND d.Id IS NULL AND x.PostId IS NULL AND b.PostId IS NULL AND g.PostId IS NULL";

    /// <summary>The lost or pending posts of a watch, newest id first.</summary>
    /// <param name="kind"><see cref="MissingLost"/> or <see cref="MissingPending"/>.</param>
    public WatchPage<MissingPost> GetMissingPosts(long watchId, string kind, int page, int pageSize)
    {
        page = Math.Max(1, page);
        pageSize = Math.Max(1, pageSize);
        using var connection = OpenWithDevice(attachServer: true);

        using var count = connection.CreateCommand();
        count.CommandText = $"SELECT COUNT(*) {MissingFrom} WHERE {MissingWhere(kind)};";
        count.Parameters.AddWithValue("$watch", watchId);
        var total = Convert.ToInt32(count.ExecuteScalar());

        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT p.Id, COALESCE(p.Md5, ''), COALESCE(p.FileExt, ''), p.IsDeleted, p.Rating, COALESCE(p.Score, 0), p.CreatedAt, p.FileSize,
                   p.ImageWidth, p.ImageHeight, COALESCE(p.TagString, ''), a.LastError, i.PostId IS NOT NULL, i.Error
            {MissingFrom}
            WHERE {MissingWhere(kind)}
            ORDER BY p.Id DESC LIMIT $limit OFFSET $offset;
            """;
        cmd.Parameters.AddWithValue("$watch", watchId);
        cmd.Parameters.AddWithValue("$limit", pageSize);
        cmd.Parameters.AddWithValue("$offset", (page - 1) * pageSize);
        var items = new List<MissingPost>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            items.Add(new MissingPost(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3) != 0, reader.GetInt32(4), reader.GetInt32(5),
                reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetInt64(7), reader.IsDBNull(8) ? null : reader.GetInt32(8), reader.IsDBNull(9) ? null : reader.GetInt32(9),
                reader.GetString(10), reader.IsDBNull(11) ? null : reader.GetString(11), reader.GetInt32(12) != 0, reader.IsDBNull(13) ? null : reader.GetString(13)));
        return new WatchPage<MissingPost>(items, total, page, pageSize);
    }

    /// <summary>Whether the post is lost or pending for the watch (so an uploaded file for it is wanted), and its catalog md5.</summary>
    public (bool Missing, string Md5) CheckMissing(long watchId, long postId)
    {
        using var connection = OpenWithDevice(attachServer: true);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT COALESCE(p.Md5, ''), (d.Id IS NULL AND (x.PostId IS NOT NULL OR (b.PostId IS NULL AND g.PostId IS NULL)))
            {MissingFrom} WHERE m.WatchedItemId = $watch AND m.PostId = $post;
            """;
        cmd.Parameters.AddWithValue("$watch", watchId);
        cmd.Parameters.AddWithValue("$post", postId);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? (reader.GetInt32(1) != 0, reader.GetString(0)) : (false, "");
    }
}
