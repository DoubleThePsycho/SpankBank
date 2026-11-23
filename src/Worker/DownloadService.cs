using SpankBank.Core;
using SpankBank.Core.Models;
using SpankBank.Data;
using SpankBank.Data.Blacklist;
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
    public const int DefaultParallelism = 3;
    public const int MaxParallelism = 64;
    private const int DefaultMaxPerDay = 2000;
    // Retry delays expire with time and the daily limit resets at midnight; a signal wakes the loop for everything else.
    private static readonly TimeSpan RetryCheckInterval = TimeSpan.FromMinutes(15);

    private readonly DownloadStore _store;
    private readonly BlockedPostsRefresher _blocked;
    private readonly SettingsRepository _settings;
    private readonly ProgressTracker _progress;
    private readonly WorkerSignals _signals;
    private readonly ILogger<DownloadService> _logger;

    public DownloadService(DownloadStore store, BlockedPostsRefresher blocked, SettingsRepository settings, ProgressTracker progress, WorkerSignals signals, ILogger<DownloadService> logger)
    {
        _store = store;
        _blocked = blocked;
        _settings = settings;
        _progress = progress;
        _signals = signals;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Directory.CreateDirectory(SpankBankPaths.Current.MediaDir);
        using var fetcher = new PostFetcher { Notice = message => _logger.LogWarning("{Message}", message) };
        using var reasons = new DeletionReasonLookup { Notice = message => _logger.LogWarning("{Message}", message) };
        if (_settings.Get(WorkerSettingsKeys.SourcesRetried) != "true")
        {
            var again = _store.RetryLostWithSources(DateTimeOffset.UtcNow);
            _settings.Set(WorkerSettingsKeys.SourcesRetried, "true");
            _logger.LogInformation("{Count:N0} post(s) marked lost have a source to try and will be looked at again.", again);
        }
        var inSession = false;
        var savedThisSession = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                RefreshBlockedIfDirty();

                // The deletion reasons of the posts on this device (shown on their pages): an API lookup, no file, so it also runs while downloads are paused.
                await ResolveDeletionReasonsAsync(reasons, _store.LocalDeletedPostsNeedingReason, stoppingToken);

                if (_settings.Get(WorkerSettingsKeys.DownloadEnabled) == "false")
                {
                    EndSession(ref inSession, "Downloads are paused.");
                    await WaitAsync(RetryCheckInterval, stoppingToken);
                    continue;
                }

                var skipInferior = _settings.Get(WorkerSettingsKeys.DownloadSkipInferiorDeleted) == "true";
                if (skipInferior) await ResolveDeletionReasonsAsync(reasons, _store.PostsNeedingDeletionReason, stoppingToken);

                var maxPerDay = int.TryParse(_settings.Get(WorkerSettingsKeys.DownloadMaxPerDay), out var configured) ? configured : DefaultMaxPerDay;
                // 0 means no daily limit.
                var room = maxPerDay <= 0 ? int.MaxValue : maxPerDay - _store.SavedToday();
                var parallelism = ReadParallelism();
                // While the Wayback Machine is backing us off, deleted posts would only be deferred one after another: leave them.
                var waybackOpen = fetcher.WaybackBlockedUntil <= DateTimeOffset.UtcNow;
                var batch = room > 0 ? _store.NextPending(Math.Min(Math.Max(24, parallelism * 8), room), DateTimeOffset.UtcNow, includeDeleted: waybackOpen, skipInferiorDeleted: skipInferior) : [];

                if (batch.Count == 0)
                {
                    var limitReached = room <= 0;
                    if (inSession)
                        _logger.LogInformation("Downloads idle: {Saved:N0} file(s) saved this session{Limit}.",
                            savedThisSession, limitReached ? "; the daily limit is reached" : "");
                    var blockedUntil = fetcher.WaybackBlockedUntil;
                    EndSession(ref inSession, limitReached ? $"Today's limit of {maxPerDay:N0} files is reached."
                        : !waybackOpen ? $"Deleted posts wait for the Wayback Machine, which asked us to back off until {blockedUntil:HH:mm} UTC."
                        : "Nothing is waiting to be downloaded.");
                    savedThisSession = 0;

                    var delay = limitReached ? NextUtcMidnight() - DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5)
                        : !waybackOpen ? blockedUntil - DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5)
                        : RetryCheckInterval;
                    await WaitAsync(delay, stoppingToken);
                    continue;
                }

                if (!inSession)
                {
                    inSession = true;
                    _progress.DownloadSessionStarted(Math.Min(_store.CountPending(DateTimeOffset.UtcNow, skipInferior), room));
                }
                else if (_progress.DownloadTotalTooSmall(batch.Count))
                {
                    // More posts turned up since the session began: count again so "n of N" stays true.
                    _progress.DownloadSessionTotal(Math.Min(_store.CountPending(DateTimeOffset.UtcNow, skipInferior), room));
                }

                await Parallel.ForEachAsync(batch,
                    new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = stoppingToken },
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

    // Missing or "true": recompute. A request made while it runs turns the flag back to "true", so it is not lost;
    // "running" after a restart means the last pass was cut short.
    private void RefreshBlockedIfDirty()
    {
        var flag = _settings.Get(WorkerSettingsKeys.BlacklistDirty);
        if (flag == "false") return;

        _settings.Set(WorkerSettingsKeys.BlacklistDirty, "running");
        var count = _blocked.Refresh();
        _settings.Replace(WorkerSettingsKeys.BlacklistDirty, "running", "false");
        _logger.LogInformation("Blacklist applied: {Count:N0} post(s) blocked.", count);
    }

    /// <summary>
    /// For the "skip inferior versions and low-effort edits" setting: finds out why the deleted posts that are waiting were
    /// deleted (50 per request, about one request a second). Posts whose reason is not known yet are held back, so a failed
    /// lookup never lets one through; it is tried again on the next pass.
    /// </summary>
    private async Task ResolveDeletionReasonsAsync(DeletionReasonLookup lookup, Func<int, IReadOnlyList<long>> pick, CancellationToken cancellationToken)
    {
        var looked = 0;
        var recognised = 0;
        // A bounded number of requests per pass, so normal downloads are never kept waiting long.
        for (var round = 0; round < 40; round++)
        {
            var ids = pick(DeletionReasonLookup.PostsPerRequest);
            if (ids.Count == 0) break;

            var found = await lookup.LookUpAsync(ids, cancellationToken);
            if (found is null) break;
            recognised += _store.SaveDeletionReasons(found);
            looked += found.Count;
        }
        if (looked > 0)
            _logger.LogInformation("Looked up why {Count:N0} deleted post(s) were deleted ({Recognised:N0} as an inferior version or a low-effort edit).", looked, recognised);
    }

    /// <summary>The "parallel downloads" setting, kept within 1-64 (default 3).</summary>
    private int ReadParallelism()
        => int.TryParse(_settings.Get(WorkerSettingsKeys.DownloadParallelism), out var value) ? Math.Clamp(value, 1, MaxParallelism) : DefaultParallelism;

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
            var outcome = await fetcher.FetchAsync(post, SpankBankPaths.Current.MediaDir, cancellationToken,
                (done, total) => _progress.FileProgress(postId, done, total),
                stage => _progress.FileStage(postId, stage));

            switch (outcome.Result)
            {
                case FetchResult.Downloaded:
                    _store.MarkSaved(post, LocalPostStatus.Downloaded);
                    saved = true;
                    status = "downloaded";
                    break;
                case FetchResult.Recovered:
                    _store.MarkSaved(post, LocalPostStatus.Recovered, outcome.RestoredFrom);
                    _logger.LogInformation("Post {Id}: {Detail}", postId, outcome.Detail);
                    saved = true;
                    status = "recovered";
                    break;
                case FetchResult.SampleOnly:
                    _store.MarkSaved(post, LocalPostStatus.SampleOnly, outcome.RestoredFrom);
                    _logger.LogInformation("Post {Id}: {Detail}", postId, outcome.Detail);
                    saved = true;
                    status = "sample";
                    break;
                case FetchResult.Deferred:
                    _store.Defer(postId, outcome.RetryAt ?? DateTimeOffset.UtcNow.AddMinutes(1), outcome.Detail);
                    status = "deferred";
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
