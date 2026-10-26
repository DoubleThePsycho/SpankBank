using System.Globalization;
using SpankBank.Data;
using SpankBank.Data.Matching;
using SpankBank.Data.Repositories;
using SpankBank.Worker.Progress;

namespace SpankBank.Worker;

/// <summary>
/// Runs the export refresh once a day, and right away when the web UI asks for it or when watches changed.
/// It sleeps until the next daily run or until the command server wakes it; nothing polls.
/// </summary>
public sealed class DailyScheduler : BackgroundService
{
    private readonly ExportRefreshJob _job;
    private readonly WatchMatcher _matcher;
    private readonly SettingsRepository _settings;
    private readonly ProgressTracker _progress;
    private readonly WorkerSignals _signals;
    private readonly ILogger<DailyScheduler> _logger;

    public DailyScheduler(
        ExportRefreshJob job,
        WatchMatcher matcher,
        SettingsRepository settings,
        ProgressTracker progress,
        WorkerSignals signals,
        ILogger<DailyScheduler> logger)
    {
        _job = job;
        _matcher = matcher;
        _settings = settings;
        _progress = progress;
        _signals = signals;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var nextRun = NextDailyRun(DateTimeOffset.UtcNow);
        var scheduledFor = _settings.Get(WorkerSettingsKeys.DailyRunTimeUtc);
        _logger.LogInformation("Worker started. Next export refresh at {NextRun:u}.", nextRun);

        // Requests made while the worker was down are waiting in the Settings table; handle them first.
        var signalled = true;
        while (!stoppingToken.IsCancellationRequested)
        {
            if (signalled || DateTimeOffset.UtcNow >= nextRun)
            {
                var runNow = _settings.Get(WorkerSettingsKeys.RunNowRequested) == "true";
                if (runNow || DateTimeOffset.UtcNow >= nextRun)
                {
                    if (runNow) _settings.Set(WorkerSettingsKeys.RunNowRequested, "false");

                    await RunJobAsync(stoppingToken);
                    nextRun = NextDailyRun(DateTimeOffset.UtcNow);
                    _logger.LogInformation("Next export refresh at {NextRun:u}.", nextRun);
                }
                else if (_settings.Get(WorkerSettingsKeys.MatchRequested) == "true")
                {
                    _settings.Set(WorkerSettingsKeys.MatchRequested, "false");
                    await RunMatchAsync(stoppingToken);
                }
                else
                {
                    // Nothing to run. A settings save also lands here: pick up a changed daily time.
                    var configured = _settings.Get(WorkerSettingsKeys.DailyRunTimeUtc);
                    if (configured != scheduledFor)
                    {
                        scheduledFor = configured;
                        nextRun = NextDailyRun(DateTimeOffset.UtcNow);
                        _logger.LogInformation("Daily update time changed. Next export refresh at {NextRun:u}.", nextRun);
                    }

                    signalled = false;
                    continue;
                }

                // Matches may have changed: the downloader should look for new work.
                _signals.Downloads.Notify();
                // Another request may have arrived while this one ran.
                signalled = true;
                continue;
            }

            try
            {
                signalled = await _signals.Scheduler.WaitAsync(nextRun - DateTimeOffset.UtcNow, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task RunMatchAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (_matcher.CountChanged() == 0) return; // e.g. only a pool was renamed

            _progress.BeginRun("Matching your watches", "Match your watches");
            _progress.StartPhase("Match your watches");
            var result = await Task.Run(() => _matcher.Run(all: false, cancellationToken,
                (scanned, total) => _progress.Step("Scanning posts", scanned, total, "posts")), cancellationToken);
            _progress.EndPhase("Match your watches");

            var text = result.WatchesMatched == 0
                ? "No changed watches."
                : $"Matched {result.PostsMatched:N0} posts for {result.WatchesMatched} changed watches.";
            _progress.EndRun(text);
            if (result.WatchesMatched > 0)
                _logger.LogInformation("{Text} ({Elapsed:g})", text, result.Elapsed);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Matching changed watches failed.");
            _progress.EndPhase("Match your watches", PhaseState.Failed);
            _progress.EndRun($"Matching failed: {ex.Message}");
        }
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
            _progress.EndRun(result);
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
}
