using Microsoft.Data.Sqlite;

namespace SpankBank.Data.Repositories;

public sealed class SettingsRepository
{
    private readonly string _connectionString;

    public SettingsRepository(string databasePath)
    {
        _connectionString = $"Data Source={databasePath}";
    }

    public string? Get(string key)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT Value FROM Settings WHERE Key = $key;";
        cmd.Parameters.AddWithValue("$key", key);
        return cmd.ExecuteScalar() as string;
    }

    public void Set(string key, string value)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO Settings (Key, Value) VALUES ($key, $value)
            ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value;
            """;
        cmd.Parameters.AddWithValue("$key", key);
        cmd.Parameters.AddWithValue("$value", value);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Sets <paramref name="key"/> to <paramref name="newValue"/> only if it currently holds <paramref name="expected"/>.</summary>
    /// <returns>True when the value was changed.</returns>
    public bool Replace(string key, string expected, string newValue)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE Settings SET Value = $new WHERE Key = $key AND Value = $expected;";
        cmd.Parameters.AddWithValue("$key", key);
        cmd.Parameters.AddWithValue("$expected", expected);
        cmd.Parameters.AddWithValue("$new", newValue);
        return cmd.ExecuteNonQuery() > 0;
    }

    /// <summary>Asks the worker for an export refresh. A new request also clears an earlier "gave up" state.</summary>
    public void RequestExportRun()
    {
        Set(WorkerSettingsKeys.RunFailures, "0");
        Set(WorkerSettingsKeys.RunNowRequested, "true");
    }
}
