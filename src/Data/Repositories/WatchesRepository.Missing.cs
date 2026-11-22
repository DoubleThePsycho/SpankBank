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

    /// <summary>Whether the post is lost or pending for the watch (so an uploaded file for it is wanted), and its catalog md5.</summary>
    public (MissingState State, string Md5) CheckMissing(long watchId, long postId)
    {
        using var connection = OpenWithDevice(attachServer: true);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT COALESCE(p.Md5, ''), m.PostId IS NOT NULL, d.Id IS NOT NULL, x.PostId IS NOT NULL, b.PostId IS NOT NULL, g.PostId IS NOT NULL
            FROM srv.Posts p
            LEFT JOIN WatchMatches m ON m.PostId = p.Id AND m.WatchedItemId = $watch
            LEFT JOIN dev.Posts d ON d.Id = p.Id AND d.SavedAt IS NOT NULL
            LEFT JOIN DownloadAttempts x ON x.PostId = p.Id AND {LostCondition}
            LEFT JOIN BlockedPosts b ON b.PostId = p.Id
            LEFT JOIN DeletedPosts g ON g.PostId = p.Id
            WHERE p.Id = $post;
            """;
        cmd.Parameters.AddWithValue("$watch", watchId);
        cmd.Parameters.AddWithValue("$post", postId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return (MissingState.NotInCatalog, "");

        var (md5, matched, local, lost, blacklisted, binned) = (reader.GetString(0), reader.GetInt32(1) != 0, reader.GetInt32(2) != 0, reader.GetInt32(3) != 0, reader.GetInt32(4) != 0, reader.GetInt32(5) != 0);
        var state = local ? MissingState.AlreadyLocal
            : !matched ? MissingState.NotListed
            : lost || (!blacklisted && !binned) ? MissingState.Missing
            : binned ? MissingState.Binned
            : MissingState.Blacklisted;
        return (state, md5);
    }
}

/// <summary>Whether an uploaded file for a post is wanted (<see cref="Missing"/>), or why not.</summary>
public enum MissingState
{
    /// <summary>Lost or pending for the artist: the file is wanted.</summary>
    Missing,
    /// <summary>A file for the post is already on this device.</summary>
    AlreadyLocal,
    /// <summary>The post is not matched by this artist's watch (another artist's, or not matched at all).</summary>
    NotListed,
    /// <summary>The id is not in the catalog.</summary>
    NotInCatalog,
    /// <summary>In the rubbish bin or deleted by the user.</summary>
    Binned,
    /// <summary>Hidden by the blacklist, so it is not on the pending list.</summary>
    Blacklisted,
}
