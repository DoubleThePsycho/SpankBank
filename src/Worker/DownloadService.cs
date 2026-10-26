using SpankBank.Core.Models;
using SpankBank.Data;
using SpankBank.Data.Downloads;
using SpankBank.Data.Repositories;
using SpankBank.Worker.Progress;

namespace SpankBank.Worker;

/// <summary>
/// Fetches the files of posts that enabled watches match and this device does not hold yet, a few at a time,
/// until the day's limit is reached. Runs next to the scheduler so a long download never delays an import.
/// </summary>
public sealed class DownloadService : BackgroundService
{
    private const int Parallelism = 3;
    private const int DefaultMaxPerDay = 2000;
    // Retry delays expire with time and the daily limit resets at midnight; a signal wakes the loop for everything else.
    private static readonly TimeSpan RetryCheckInterval = TimeSpan.FromMinutes(15);

    private readonly DownloadStore _store;
    private readonly SettingsRepository _settings;
    private readonly ProgressTracker _progress;
    private readonly WorkerSignals _signals;
    private readonly ILogger<DownloadService> _logger;

    public DownloadService(DownloadStore store, SettingsRepository settings, ProgressTracker progress, WorkerSignals signals, ILogger<DownloadService> logger)
    {
        _store = store;
        _settings = settings;
        _progress = progress;
        _signals = signals;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Directory.CreateDirectory(WorkerPaths.MediaDir);
        using var fetcher = new PostFetcher();
        var inSession = false;
        var savedThisSession = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (_settings.Get(WorkerSettingsKeys.DownloadEnabled) == "false")
                {
                    EndSession(ref inSession, "Downloads are paused.");
                    await WaitAsync(RetryCheckInterval, stoppingToken);
                    continue;
                }

                var maxPerDay = int.TryParse(_settings.Get(WorkerSettingsKeys.DownloadMaxPerDay), out var configured) ? configured : DefaultMaxPerDay;
                // 0 means no daily limit.
                var room = maxPerDay <= 0 ? int.MaxValue : maxPerDay - _store.SavedToday();
                var batch = room > 0 ? _store.NextPending(Math.Min(24, room), DateTimeOffset.UtcNow) : [];

                if (batch.Count == 0)
                {
                    var limitReached = room <= 0;
                    if (inSession)
                        _logger.LogInformation("Downloads idle: {Saved:N0} file(s) saved this session{Limit}.",
                            savedThisSession, limitReached ? "; the daily limit is reached" : "");
                    EndSession(ref inSession, limitReached ? $"Today's limit of {maxPerDay:N0} files is reached." : "Nothing is waiting to be downloaded.");
                    savedThisSession = 0;

                    var delay = limitReached ? NextUtcMidnight() - DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5) : RetryCheckInterval;
                    await WaitAsync(delay, stoppingToken);
                    continue;
                }

                if (!inSession)
                {
                    inSession = true;
                    _progress.DownloadSessionStarted(Math.Min(_store.CountPending(DateTimeOffset.UtcNow), room));
                }

                await Parallel.ForEachAsync(batch,
                    new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = stoppingToken },
                    async (id, token) =>
                    {
                        if (await ProcessAsync(id, fetcher, token)) Interlocked.Increment(ref savedThisSession);
                    });
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Download loop failed; retrying in a moment.");
                await WaitAsync(TimeSpan.FromSeconds(30), stoppingToken);
            }
        }
    }

    private void EndSession(ref bool inSession, string reason)
    {
        inSession = false;
        _progress.DownloadSessionIdle(reason);
    }

    private async Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            await _signals.Downloads.WaitAsync(timeout, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private static DateTimeOffset NextUtcMidnight() => new DateTimeOffset(DateTimeOffset.UtcNow.Date, TimeSpan.Zero).AddDays(1);

    /// <returns>True when a file was saved.</returns>
    private async Task<bool> ProcessAsync(long postId, PostFetcher fetcher, CancellationToken cancellationToken)
    {
        var post = (CatalogPost?)null;
        var saved = false;
        var status = "";
        try
        {
            post = _store.GetCatalogPost(postId);
            if (post is null)
            {
                _store.RecordFailure(postId, "The post is not in the catalog.", TimeSpan.FromDays(1));
                _progress.FileFinished(postId, false, "", "", "missing");
                return false;
            }

            _progress.FileStarted(postId, $"{post.Md5}.{post.Ext}");
            var outcome = await fetcher.FetchAsync(post, WorkerPaths.MediaDir, cancellationToken,
                (done, total) => _progress.FileProgress(postId, done, total));

            switch (outcome.Result)
            {
                case FetchResult.Downloaded:
                    _store.MarkSaved(post, LocalPostStatus.Downloaded);
                    saved = true;
                    status = "downloaded";
                    break;
                case FetchResult.Recovered:
                    _store.MarkSaved(post, LocalPostStatus.Recovered);
                    _logger.LogInformation("Post {Id}: {Detail}", postId, outcome.Detail);
                    saved = true;
                    status = "recovered";
                    break;
                case FetchResult.SampleOnly:
                    _store.MarkSaved(post, LocalPostStatus.SampleOnly);
                    _logger.LogInformation("Post {Id}: {Detail}", postId, outcome.Detail);
                    saved = true;
                    status = "sample";
                    break;
                case FetchResult.NotAvailable:
                    // Posts do come back (takedowns get withdrawn) and archives grow, so look again in a month.
                    _store.RecordFailure(postId, outcome.Detail, TimeSpan.FromDays(30));
                    status = "unavailable";
                    break;
                default:
                    _store.RecordFailure(postId, outcome.Detail, RetryDelay(_store.AttemptsSoFar(postId)));
                    _logger.LogWarning("Post {Id}: {Detail}", postId, outcome.Detail);
                    status = "failed";
                    break;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _store.RecordFailure(postId, ex.Message, RetryDelay(_store.AttemptsSoFar(postId)));
            _logger.LogError(ex, "Post {Id} failed.", postId);
            status = "failed";
        }

        _progress.FileFinished(postId, saved, post?.Md5 ?? "", post?.Ext ?? "", status);
        return saved;
    }

    private static TimeSpan RetryDelay(int attemptsSoFar) => attemptsSoFar switch
    {
        0 => TimeSpan.FromMinutes(15),
        1 => TimeSpan.FromHours(1),
        2 => TimeSpan.FromHours(6),
        3 => TimeSpan.FromHours(24),
        _ => TimeSpan.FromDays(3),
    };
}
