namespace SpankBank.Worker;

/// <summary>Container paths. Map them to host folders with <c>volumes:</c> in compose.yaml.</summary>
public static class WorkerPaths
{
    // SPANKBANK_DATA_DIR only exists so the worker can run outside Docker during development.
    public static readonly string DataDir = Environment.GetEnvironmentVariable("SPANKBANK_DATA_DIR") ?? "/data";
    public static readonly string DbDir = Path.Combine(DataDir, "db");
    public static readonly string ImportsDir = Path.Combine(DataDir, "imports");
}
