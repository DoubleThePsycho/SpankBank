namespace SpankBank.Data;

/// <summary>
/// Where an instance keeps its files, the same for the worker and the web app: <c>/data</c> (databases in <c>db</c>,
/// export downloads in <c>imports</c>, the web app's key ring in <c>keys</c>) and <c>/media</c> (downloads, previews,
/// samples and the rubbish bin). Compose maps them to host folders. <c>SPANKBANK_DATA_DIR</c> and <c>SPANKBANK_MEDIA_DIR</c>
/// override them, only so the apps can run outside Docker during development.
/// </summary>
public sealed class SpankBankPaths
{
    public static SpankBankPaths Current { get; } = new(Environment.GetEnvironmentVariable);

    public SpankBankPaths(Func<string, string?> get)
    {
        DataDir = Folder(get("SPANKBANK_DATA_DIR"), "/data");
        MediaDir = Folder(get("SPANKBANK_MEDIA_DIR"), "/media");
    }

    public string DataDir { get; }
    public string MediaDir { get; }
    public string DbDir => Path.Combine(DataDir, "db");
    public string ImportsDir => Path.Combine(DataDir, "imports");
    /// <summary>ASP.NET Data Protection key ring (antiforgery tokens).</summary>
    public string KeysDir => Path.Combine(DataDir, "keys");

    /// <summary>The three database files in <see cref="DbDir"/> (the folder is created if missing).</summary>
    public DatabasePaths Databases() => DatabaseInitializer.GetDatabasePaths(DbDir);

    private static string Folder(string? value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value;
}
