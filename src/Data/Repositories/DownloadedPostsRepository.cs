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
            SELECT Id, Rating, CreatedAt, UpdatedAt, Score, FileExt, Md5
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
                FavCount: 0
            ));
        }
        return results;
    }
}
