using Microsoft.Data.Sqlite;

namespace SpankBank.Data.Import;

public sealed class ImportHistoryRepository
{
    private readonly string _connectionString;

    public ImportHistoryRepository(string databasePath)
    {
        _connectionString = $"Data Source={databasePath}";
    }

    public void Add(ImportSmokeTestResult result)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO ImportHistory
                (ImportType, SourcePath, StartedAt, CompletedAt, Succeeded, RowsProcessed, ErrorMessage)
            VALUES
                ($importType, $sourcePath, $startedAt, $completedAt, $succeeded, $rowsProcessed, $errorMessage);
            """;
        cmd.Parameters.AddWithValue("$importType", result.ImportType);
        cmd.Parameters.AddWithValue("$sourcePath", result.SourcePath);
        cmd.Parameters.AddWithValue("$startedAt", result.StartedAt.ToUniversalTime().ToString("o"));
        cmd.Parameters.AddWithValue("$completedAt", result.CompletedAt.ToUniversalTime().ToString("o"));
        cmd.Parameters.AddWithValue("$succeeded", result.Succeeded ? 1 : 0);
        cmd.Parameters.AddWithValue("$rowsProcessed", result.RowsProcessed);
        cmd.Parameters.AddWithValue("$errorMessage", (object?)result.ErrorMessage ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public ImportHistoryEntry? GetLatest()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT Id, ImportType, SourcePath, StartedAt, CompletedAt, Succeeded, RowsProcessed, ErrorMessage
            FROM ImportHistory
            ORDER BY StartedAt DESC, Id DESC
            LIMIT 1;
            """;

        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return null;

        return new ImportHistoryEntry(
            Id: reader.GetInt64(0),
            ImportType: reader.GetString(1),
            SourcePath: reader.GetString(2),
            StartedAt: DateTimeOffset.Parse(reader.GetString(3)),
            CompletedAt: reader.IsDBNull(4) ? null : DateTimeOffset.Parse(reader.GetString(4)),
            Succeeded: reader.GetInt32(5) != 0,
            RowsProcessed: reader.GetInt64(6),
            ErrorMessage: reader.IsDBNull(7) ? null : reader.GetString(7)
        );
    }

    public bool HasSuccessfulImport(string importType, string fileName)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT 1
            FROM ImportHistory
            WHERE ImportType = $importType
              AND Succeeded = 1
              AND SourcePath LIKE $sourceSuffix
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$importType", importType);
        cmd.Parameters.AddWithValue("$sourceSuffix", $"%{fileName}");
        return cmd.ExecuteScalar() is not null;
    }

    public bool HasAnySuccessfulE621Import()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT 1
            FROM ImportHistory
            WHERE Succeeded = 1
              AND ImportType IN ('tags', 'tag_aliases')
            LIMIT 1;
            """;
        return cmd.ExecuteScalar() is not null;
    }

    public IReadOnlySet<string> GetSuccessfullyImportedFileNames(IReadOnlyCollection<string> importTypes)
    {
        if (importTypes.Count == 0)
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        var placeholders = importTypes.Select((_, i) => $"$type{i}").ToArray();
        cmd.CommandText = $"""
            SELECT SourcePath
            FROM ImportHistory
            WHERE Succeeded = 1
              AND ImportType IN ({string.Join(", ", placeholders)});
            """;

        var index = 0;
        foreach (var importType in importTypes)
            cmd.Parameters.AddWithValue(placeholders[index++], importType);

        using var reader = cmd.ExecuteReader();
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read())
        {
            var fileName = Path.GetFileName(reader.GetString(0));
            if (!string.IsNullOrWhiteSpace(fileName))
                files.Add(fileName);
        }

        return files;
    }
}
