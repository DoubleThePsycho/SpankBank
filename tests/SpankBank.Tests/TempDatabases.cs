using Microsoft.Data.Sqlite;
using SpankBank.Data;

namespace SpankBank.Tests;

/// <summary>Fresh, initialised databases in a temp folder, deleted on dispose.</summary>
public sealed class TempDatabases : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "spankbank-tests-" + Guid.NewGuid().ToString("N"));

    public DatabasePaths Paths { get; }

    public TempDatabases()
    {
        Paths = DatabaseInitializer.GetDatabasePaths(_folder);
        DatabaseInitializer.Initialize(Paths);
    }

    public void Execute(string databasePath, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public long Scalar(string databasePath, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }
}
