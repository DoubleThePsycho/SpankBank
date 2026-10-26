using Microsoft.Data.Sqlite;

namespace SpankBank.Data.Repositories;

/// <summary>The user's favorite posts (SpankBank.sqlite). Keyed by post id; the post itself lives in device.sqlite.</summary>
public sealed class FavoritesRepository
{
    private readonly string _connectionString;

    public FavoritesRepository(string appDatabasePath)
    {
        _connectionString = $"Data Source={appDatabasePath}";
    }

    public bool IsFavorite(long postId)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM Favorites WHERE PostId = $id;";
        cmd.Parameters.AddWithValue("$id", postId);
        return cmd.ExecuteScalar() is not null;
    }

    /// <summary>Adds the favorite; does nothing when it already exists.</summary>
    public void Add(long postId)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "INSERT OR IGNORE INTO Favorites (PostId, CreatedAt) VALUES ($id, $now);";
        cmd.Parameters.AddWithValue("$id", postId);
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    public void Remove(long postId)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM Favorites WHERE PostId = $id;";
        cmd.Parameters.AddWithValue("$id", postId);
        cmd.ExecuteNonQuery();
    }
}
