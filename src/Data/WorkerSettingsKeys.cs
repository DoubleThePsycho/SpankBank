namespace SpankBank.Data;

public static class WorkerSettingsKeys
{
    /// <summary>Set to "true" (by the web UI) to request an immediate export refresh.</summary>
    public const string RunNowRequested = "Worker.RunNowRequested";
    /// <summary>Set to "true" (by the web UI) after a watch changed; the worker re-matches the changed watches.</summary>
    public const string MatchRequested = "Worker.MatchRequested";
    public const string DailyRunTimeUtc = "Worker.DailyRunTimeUtc";
    public const string LastRunStartedAt = "Worker.LastRunStartedAt";
    public const string LastRunCompletedAt = "Worker.LastRunCompletedAt";
    public const string LastRunResult = "Worker.LastRunResult";

    /// <summary>"false" pauses the downloader (default: on).</summary>
    public const string DownloadEnabled = "Download.Enabled";
    /// <summary>Most files fetched per UTC day (default 2000), so a new watch cannot hammer e621 in one go.</summary>
    public const string DownloadMaxPerDay = "Download.MaxPerDay";
}
