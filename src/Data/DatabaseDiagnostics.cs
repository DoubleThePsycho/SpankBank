namespace SpankBank.Data;

public sealed class DatabaseDiagnostics
{
    public string Path { get; init; } = string.Empty;
    public bool FileExists { get; init; }
    public long FileSizeBytes { get; init; }
    public bool InitializationSucceeded { get; init; }
    public string? InitializationError { get; init; }
    public IReadOnlyList<string> Tables { get; init; } = [];

    public string FileSizeDisplay => FileSizeBytes switch
    {
        0 => "-",
        < 1024 => $"{FileSizeBytes} B",
        < 1024 * 1024 => $"{FileSizeBytes / 1024.0:F1} KB",
        _ => $"{FileSizeBytes / (1024.0 * 1024.0):F2} MB"
    };
}
