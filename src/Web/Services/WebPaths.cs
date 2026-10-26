using SpankBank.Data;

namespace SpankBank.Web.Services;

public static class WebPaths
{
    // SPANKBANK_DATA_DIR only exists so the web app can run outside Docker during development.
    public static readonly string DataDir = Environment.GetEnvironmentVariable("SPANKBANK_DATA_DIR") ?? "/data";
    public static readonly string DbDir = Path.Combine(DataDir, "db");
    public static readonly string KeysDir = Path.Combine(DataDir, "keys"); // ASP.NET Data Protection key ring (antiforgery tokens)
    public static readonly string MediaDir = Path.Combine(DataDir, "media");

    public static DatabasePaths Databases() => DatabaseInitializer.GetDatabasePaths(DbDir);
}
