using System.Globalization;
using SpankBank.Data;
using SpankBank.Data.Repositories;

namespace SpankBank.Worker;

/// <summary>Runs the export refresh once a day, or immediately when the run-now flag is set.</summary>
public sealed class DailyScheduler : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);

    private readonly ExportRefreshJob _job;
    private readonly SettingsRepository _settings;
    private readonly ILogger<DailyScheduler> _logger;

    public DailyScheduler(
        ExportRefreshJob job,
        SettingsRepository settings,
        ILogger<DailyScheduler> logger)
    {
        _job = job;
        _settings = settings;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var nextRun = NextDailyRun(DateTimeOffset.UtcNow);
        _logger.LogInformation("Worker started. Next export refresh at {NextRun:u}.", nextRun);

        using var timer = new PeriodicTimer(PollInterval);
        do
        {
            var runNow = _settings.Get(WorkerSettingsKeys.RunNowRequested) == "true";
            if (runNow || DateTimeOffset.UtcNow >= nextRun)
            {
                if (runNow)
                    _settings.Set(WorkerSettingsKeys.RunNowRequested, "false");

                await RunJobAsync(stoppingToken);
                nextRun = NextDailyRun(DateTimeOffset.UtcNow);
                _logger.LogInformation("Next export refresh at {NextRun:u}.", nextRun);
            }
        }
        while (await WaitAsync(timer, stoppingToken));
    }

    private async Task RunJobAsync(CancellationToken cancellationToken)
    {
        _settings.Set(WorkerSettingsKeys.LastRunStartedAt, DateTimeOffset.UtcNow.ToString("o"));
        string result;
        try
        {
            result = await _job.RunAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Export refresh crashed.");
            result = $"Crashed: {ex.Message}";
        }

        _settings.Set(WorkerSettingsKeys.LastRunCompletedAt, DateTimeOffset.UtcNow.ToString("o"));
        _settings.Set(WorkerSettingsKeys.LastRunResult, result);
        _logger.LogInformation("Export refresh finished: {Result}", result);
    }

    private DateTimeOffset NextDailyRun(DateTimeOffset now)
    {
        // Default 04:00 UTC; the Settings table (editable from the web UI later) overrides it.
        var configured = _settings.Get(WorkerSettingsKeys.DailyRunTimeUtc);
        if (!TimeOnly.TryParseExact(configured, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
            time = new TimeOnly(4, 0);

        var today = new DateTimeOffset(now.Year, now.Month, now.Day, time.Hour, time.Minute, 0, TimeSpan.Zero);
        return today > now ? today : today.AddDays(1);
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken cancellationToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
