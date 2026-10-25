namespace SpankBank.Data.Import;

public sealed record ImportHistoryEntry(
    long Id,
    string ImportType,
    string SourcePath,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    bool Succeeded,
    long RowsProcessed,
    string? ErrorMessage
);

public sealed record ImportSmokeTestResult(
    string ImportType,
    string SourcePath,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    bool Succeeded,
    long RowsProcessed,
    string? ErrorMessage
);

public sealed record TagsImportResult(
    ImportSmokeTestResult History,
    long RowsRead,
    long RowsImportedOrUpdated,
    long RowsSkippedZeroPostCount
);

public sealed record TagAliasesImportResult(
    ImportSmokeTestResult History,
    long RowsRead,
    long ActiveRows,
    long TagsUpdated
);

public sealed record PostsImportResult(
    ImportSmokeTestResult History,
    long RowsRead,
    long RowsImported
);

public sealed record PoolsImportResult(
    ImportSmokeTestResult History,
    long RowsRead,
    long RowsImported
);

public sealed record PostsImportProgress(
    long RowsRead,
    long RowsImported,
    string Message);

public sealed record TagAliasesImportProgress(
    long RowsRead,
    long ActiveRows,
    long TagsUpdated,
    string Message);
