using Microsoft.Data.Sqlite;
using SpankBank.Core.Models;

namespace SpankBank.Data.Repositories;

/// <summary>Posts that exist on this device (device.sqlite). The catalog in server.sqlite is never listed.</summary>
public sealed class DownloadedPostsRepository
{
    private const string DownloadedCondition = "SavedAt IS NOT NULL";

    private readonly string _connectionString;

    public DownloadedPostsRepository(string deviceDatabasePath)
    {
        _connectionString = $"Data Source={deviceDatabasePath}";
    }

    public long CountPosts()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM Posts WHERE {DownloadedCondition};";
        return (long)(cmd.ExecuteScalar() ?? 0L);
    }

    public IReadOnlyList<PostListItem> GetRecentlyDownloaded(int limit, int offset = 0)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT Id, Rating, CreatedAt, UpdatedAt, Score, FileExt, Md5, Duration, TagString
            FROM Posts
            WHERE {DownloadedCondition}
            ORDER BY SavedAt DESC, Id DESC
            LIMIT $limit OFFSET $offset;
            """;
        cmd.Parameters.AddWithValue("$limit", limit);
        cmd.Parameters.AddWithValue("$offset", offset);

        using var reader = cmd.ExecuteReader();
        var results = new List<PostListItem>();
        while (reader.Read())
        {
            results.Add(new PostListItem(
                Id: reader.GetInt64(0),
                Rating: (PostRating)reader.GetInt32(1),
                CreatedAt: DateTimeOffset.Parse(reader.GetString(2)),
                UpdatedAt: DateTimeOffset.Parse(reader.GetString(3)),
                Score: reader.GetInt32(4),
                FileExt: reader.IsDBNull(5) ? null : reader.GetString(5),
                Md5: reader.IsDBNull(6) ? null : reader.GetString(6),
                FavCount: 0,
                Duration: reader.IsDBNull(7) ? null : reader.GetDouble(7),
                TagString: reader.IsDBNull(8) ? null : reader.GetString(8)
            ));
        }
        return results;
    }

    public DownloadedPostDetail? GetPost(long id)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT Id, Md5, FileExt, Rating, Score, ImageWidth, ImageHeight, FileSize, TagString, Description,
                   ParentId, CreatedAt, SavedAt, Status, Duration
            FROM Posts WHERE Id = $id AND SavedAt IS NOT NULL;
            """;
        cmd.Parameters.AddWithValue("$id", id);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;

        return new DownloadedPostDetail(
            Id: reader.GetInt64(0),
            Md5: reader.IsDBNull(1) ? "" : reader.GetString(1),
            FileExt: reader.IsDBNull(2) ? "" : reader.GetString(2),
            Rating: (PostRating)reader.GetInt32(3),
            Score: reader.GetInt32(4),
            Width: reader.IsDBNull(5) ? null : reader.GetInt32(5),
            Height: reader.IsDBNull(6) ? null : reader.GetInt32(6),
            FileSize: reader.IsDBNull(7) ? null : reader.GetInt64(7),
            TagString: reader.IsDBNull(8) ? "" : reader.GetString(8),
            Description: reader.IsDBNull(9) ? null : reader.GetString(9),
            ParentId: reader.IsDBNull(10) ? null : reader.GetInt64(10),
            CreatedAt: DateTimeOffset.Parse(reader.GetString(11)),
            SavedAt: DateTimeOffset.Parse(reader.GetString(12)),
            Status: (LocalPostStatus)reader.GetInt32(13),
            Duration: reader.IsDBNull(14) ? null : reader.GetDouble(14));
    }

    /// <summary>Ids of downloaded posts whose parent is the given post.</summary>
    public IReadOnlyList<long> GetChildIds(long parentId)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT Id FROM Posts WHERE ParentId = $id AND SavedAt IS NOT NULL ORDER BY Id;";
        cmd.Parameters.AddWithValue("$id", parentId);
        using var reader = cmd.ExecuteReader();
        var ids = new List<long>();
        while (reader.Read()) ids.Add(reader.GetInt64(0));
        return ids;
    }

    public bool Exists(long id)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM Posts WHERE Id = $id AND SavedAt IS NOT NULL;";
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteScalar() is not null;
    }
}

public sealed record DownloadedPostDetail(
    long Id,
    string Md5,
    string FileExt,
    PostRating Rating,
    int Score,
    int? Width,
    int? Height,
    long? FileSize,
    string TagString,
    string? Description,
    long? ParentId,
    DateTimeOffset CreatedAt,
    DateTimeOffset SavedAt,
    LocalPostStatus Status,
    double? Duration);
