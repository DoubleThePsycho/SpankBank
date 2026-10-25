namespace SpankBank.Worker;

public static class WorkerSettingsKeys
{
    /// <summary>Set to "true" (by the web UI) to request an immediate export refresh.</summary>
    public const string RunNowRequested = "Worker.RunNowRequested";
    public const string DailyRunTimeUtc = "Worker.DailyRunTimeUtc";
    public const string LastRunStartedAt = "Worker.LastRunStartedAt";
    public const string LastRunCompletedAt = "Worker.LastRunCompletedAt";
    public const string LastRunResult = "Worker.LastRunResult";
}
