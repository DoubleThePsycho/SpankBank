using SpankBank.Core.Models;
using SpankBank.Data;
using SpankBank.Data.Downloads;
using SpankBank.Data.Repositories;

namespace SpankBank.Worker;

/// <summary>
/// Fetches the files of posts that enabled watches match and this device does not hold yet, a few at a time,
/// until the day's limit is reached. Runs next to the scheduler so a long download never delays an import.
/// </summary>
public sealed class DownloadService : BackgroundService
{
    private const int Parallelism = 3;
    private const int DefaultMaxPerDay = 2000;
    private static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(30);

    private readonly DownloadStore _store;
    private readonly SettingsRepository _settings;
    private readonly ILogger<DownloadService> _logger;

    public DownloadService(DownloadStore store, SettingsRepository settings, ILogger<DownloadService> logger)
    {
        _store = store;
        _settings = settings;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Directory.CreateDirectory(WorkerPaths.MediaDir);
        using var fetcher = new PostFetcher();
        var savedThisSession = 0;
        var wasBusy = false;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (_settings.Get(WorkerSettingsKeys.DownloadEnabled) == "false")
                {
                    await Task.Delay(IdleDelay, stoppingToken);
                    continue;
                }

                var maxPerDay = int.TryParse(_settings.Get(WorkerSettingsKeys.DownloadMaxPerDay), out var configured) ? configured : DefaultMaxPerDay;
                var room = maxPerDay - _store.SavedToday();
                var batch = room > 0 ? _store.NextPending(Math.Min(24, room), DateTimeOffset.UtcNow) : [];

                if (batch.Count == 0)
                {
                    if (wasBusy)
                    {
                        _logger.LogInformation("Downloads idle: {Saved:N0} file(s) saved this session; {Pending:N0} post(s) still waiting{Limit}.",
                            savedThisSession, _store.CountPending(DateTimeOffset.UtcNow), room <= 0 ? " (daily limit reached)" : "");
                        wasBusy = false;
                    }
                    await Task.Delay(room <= 0 ? TimeSpan.FromMinutes(5) : IdleDelay, stoppingToken);
                    continue;
                }

                wasBusy = true;
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
                await Task.Delay(IdleDelay, stoppingToken);
            }
        }
    }

    /// <returns>True when a file was saved.</returns>
    private async Task<bool> ProcessAsync(long postId, PostFetcher fetcher, CancellationToken cancellationToken)
    {
        try
        {
            var post = _store.GetCatalogPost(postId);
            if (post is null)
            {
                _store.RecordFailure(postId, "The post is not in the catalog.", TimeSpan.FromDays(1));
                return false;
            }

            var outcome = await fetcher.FetchAsync(post, WorkerPaths.MediaDir, cancellationToken);
            switch (outcome.Result)
            {
                case FetchResult.Downloaded:
                    _store.MarkSaved(post, LocalPostStatus.Downloaded);
                    return true;
                case FetchResult.Recovered:
                    _store.MarkSaved(post, LocalPostStatus.Recovered);
                    _logger.LogInformation("Post {Id}: {Detail}", postId, outcome.Detail);
                    return true;
                case FetchResult.SampleOnly:
                    _store.MarkSaved(post, LocalPostStatus.SampleOnly);
                    _logger.LogInformation("Post {Id}: {Detail}", postId, outcome.Detail);
                    return true;
                case FetchResult.NotAvailable:
                    // Posts do come back (takedowns get withdrawn) and archives grow, so look again in a month.
                    _store.RecordFailure(postId, outcome.Detail, TimeSpan.FromDays(30));
                    return false;
                default:
                    _store.RecordFailure(postId, outcome.Detail, RetryDelay(_store.AttemptsSoFar(postId)));
                    _logger.LogWarning("Post {Id}: {Detail}", postId, outcome.Detail);
                    return false;
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
            return false;
        }
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
