using Microsoft.Data.Sqlite;

namespace SpankBank.Data;

// DEV ONLY - inserts clearly-marked sample posts using negative IDs.
// Negative IDs cannot occur in real e621 data (all real post IDs are positive),
// so these rows are safe to identify and remove without touching imported data.
public sealed class DevelopmentSeedService
{
    private readonly string _connectionString;

    public DevelopmentSeedService(string databasePath)
    {
        _connectionString = $"Data Source={databasePath}";
    }

    public int GetSamplePostCount()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM Posts WHERE Id < 0;";
        return (int)(long)(cmd.ExecuteScalar() ?? 0L);
    }

    public void SeedSamplePosts()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var tx = connection.BeginTransaction();

        var samples = new[]
        {
            (-1L, "2024-01-15T10:00:00+00:00", "2024-03-20T14:30:00+00:00",  95, 1, "png",  800,  600),
            (-2L, "2024-02-10T08:00:00+00:00", "2024-04-05T09:15:00+00:00",  42, 2, "jpg", 1920, 1080),
            (-3L, "2024-03-01T12:00:00+00:00", "2024-04-10T16:45:00+00:00", 120, 0, "png", 1280,  720),
            (-4L, "2024-03-22T18:00:00+00:00", "2024-05-01T11:00:00+00:00",  15, 1, "webm", 1920, 1080),
            (-5L, "2024-04-14T22:00:00+00:00", "2024-05-08T20:00:00+00:00",  77, 0, "swf",  640,  480),
        };

        foreach (var (id, createdAt, updatedAt, score, rating, ext, width, height) in samples)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                INSERT OR IGNORE INTO Posts
                    (Id, CreatedAt, UpdatedAt, Score, Rating, FileExt, FileWidth, FileHeight)
                VALUES
                    ($id, $createdAt, $updatedAt, $score, $rating, $ext, $width, $height);
                """;
            cmd.Parameters.AddWithValue("$id",        id);
            cmd.Parameters.AddWithValue("$createdAt", createdAt);
            cmd.Parameters.AddWithValue("$updatedAt", updatedAt);
            cmd.Parameters.AddWithValue("$score",     score);
            cmd.Parameters.AddWithValue("$rating",    rating);
            cmd.Parameters.AddWithValue("$ext",       ext);
            cmd.Parameters.AddWithValue("$width",     width);
            cmd.Parameters.AddWithValue("$height",    height);
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
    }

    public int ClearSamplePosts()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM Posts WHERE Id < 0;";
        return cmd.ExecuteNonQuery();
    }
}
