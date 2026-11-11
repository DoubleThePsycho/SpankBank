namespace SpankBank.Data;

public static class WorkerSettingsKeys
{
    /// <summary>
    /// "true" (set by the web UI) requests an immediate export refresh. The worker flips it to "running" while it works,
    /// so a request made during the run is not lost, and back to "false" only when the run succeeded or gave up.
    /// </summary>
    public const string RunNowRequested = "Worker.RunNowRequested";
    /// <summary>Failed attempts since the last request or success; the worker gives up after <c>DailyScheduler.MaxAttempts</c>.</summary>
    public const string RunFailures = "Worker.RunFailures";
    /// <summary>Set to "true" (by the web UI) after a watch changed; the worker re-matches the changed watches.</summary>
    public const string MatchRequested = "Worker.MatchRequested";
    /// <summary>"true" (or missing) when the blocked posts must be recomputed; the downloader does it before its next batch, "running" while it does.</summary>
    public const string BlacklistDirty = "Blacklist.Dirty";
    /// <summary>"true" lets the posts pages delete (move to the rubbish bin); default off.</summary>
    public const string DeleteModeEnabled = "Bin.DeleteModeEnabled";
    /// <summary>Rows per page in the artist and pool lists (default 100).</summary>
    public const string ListPageSize = "Ui.ListPageSize";
    /// <summary>The site theme (hexagon, pony, bloodlust, ...); default hexagon. Stored per instance, not tied to a site.</summary>
    public const string UiTheme = "Ui.Theme";
    /// <summary>The embellishment drawn over the theme (hexagon, none, autumn, ...); default hexagon.</summary>
    public const string UiThemeExtra = "Ui.ThemeExtra";
    public const string DailyRunTimeUtc = "Worker.DailyRunTimeUtc";
    public const string LastRunStartedAt = "Worker.LastRunStartedAt";
    public const string LastRunCompletedAt = "Worker.LastRunCompletedAt";
    public const string LastRunResult = "Worker.LastRunResult";

    /// <summary>"true" once the posts that were lost before sources were tried have been queued for another look.</summary>
    public const string SourcesRetried = "Download.SourcesRetried";
    /// <summary>"false" pauses the downloader (default: on).</summary>
    public const string DownloadEnabled = "Download.Enabled";
    /// <summary>Most files fetched per UTC day (default 2000), so a new watch cannot hammer the site in one go.</summary>
    public const string DownloadMaxPerDay = "Download.MaxPerDay";
    /// <summary>Files fetched at the same time (1-64, default 3); read before every batch, so a change applies without a restart.</summary>
    public const string DownloadParallelism = "Download.Parallelism";

    /// <summary>"false" turns off adding an artist's pools automatically (default: on).</summary>
    public const string PoolsAutoAddEnabled = "Pools.AutoAddEnabled";
    /// <summary>A pool is added automatically when at least this percent of its posts are the artist's (default 90).</summary>
    public const string PoolsAutoAddPercent = "Pools.AutoAddPercent";
}
