using Microsoft.Data.Sqlite;
using SpankBank.Core.Models;

namespace SpankBank.Data.Repositories;

/// <summary>A matched post that is not on this device: lost (deleted on the site, no archive has it) or pending (still to be fetched).</summary>
/// <param name="IsLost">Lost (deleted and no archive has it) rather than pending.</param>
/// <param name="LastError">Why the last attempt failed (the lost reason), if any.</param>
/// <param name="QueuedImport">An uploaded file is waiting for the worker.</param>
/// <param name="ImportError">The worker could not use the uploaded file.</param>
public sealed record MissingPost(long Id, string Md5, string Ext, bool IsDeleted, bool IsLost, int Rating, int Score, string CreatedAt, long? FileSize,
    int? Width, int? Height, string TagString, string? LastError, bool QueuedImport, string? ImportError);

public sealed partial class WatchesRepository
{
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

    private const string MissingWhere = "m.WatchedItemId = $watch AND d.Id IS NULL AND (x.PostId IS NOT NULL OR (b.PostId IS NULL AND g.PostId IS NULL))";

    /// <summary>The lost and pending posts of a watch together, newest id first.</summary>
    public WatchPage<MissingPost> GetMissingPosts(long watchId, int page, int pageSize)
    {
        page = Math.Max(1, page);
        pageSize = Math.Max(1, pageSize);
        using var connection = OpenWithDevice(attachServer: true);

        using var count = connection.CreateCommand();
        count.CommandText = $"SELECT COUNT(*) {MissingFrom} WHERE {MissingWhere};";
        count.Parameters.AddWithValue("$watch", watchId);
        var total = Convert.ToInt32(count.ExecuteScalar());

        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT p.Id, COALESCE(p.Md5, ''), COALESCE(p.FileExt, ''), p.IsDeleted, x.PostId IS NOT NULL, p.Rating, COALESCE(p.Score, 0), p.CreatedAt, p.FileSize,
                   p.ImageWidth, p.ImageHeight, COALESCE(p.TagString, ''), a.LastError, i.PostId IS NOT NULL, i.Error
            {MissingFrom}
            WHERE {MissingWhere}
            ORDER BY p.Id DESC LIMIT $limit OFFSET $offset;
            """;
        cmd.Parameters.AddWithValue("$watch", watchId);
        cmd.Parameters.AddWithValue("$limit", pageSize);
        cmd.Parameters.AddWithValue("$offset", (page - 1) * pageSize);
        var items = new List<MissingPost>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            items.Add(new MissingPost(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3) != 0, reader.GetInt32(4) != 0, reader.GetInt32(5), reader.GetInt32(6),
                reader.GetString(7), reader.IsDBNull(8) ? null : reader.GetInt64(8), reader.IsDBNull(9) ? null : reader.GetInt32(9), reader.IsDBNull(10) ? null : reader.GetInt32(10),
                reader.GetString(11), reader.IsDBNull(12) ? null : reader.GetString(12), reader.GetInt32(13) != 0, reader.IsDBNull(14) ? null : reader.GetString(14)));
        return new WatchPage<MissingPost>(items, total, page, pageSize);
    }
}
