using SpankBank.Data;
using SpankBank.Data.RubbishBin;
using SpankBank.Worker.Progress;

namespace SpankBank.Worker;

/// <summary>
/// Carries out the rubbish bin requests the web app records (delete, restore, empty) and purges posts that have
/// been in the bin for 30 days. Sleeps until signalled; the hourly wake-up only catches the 30-day expiry.
/// </summary>
public sealed class BinService : BackgroundService
{
    private static readonly TimeSpan ExpiryCheckInterval = TimeSpan.FromHours(1);

    private readonly BinStore _bin;
    private readonly WorkerSignals _signals;
    private readonly ProgressTracker _progress;
    private readonly ILogger<BinService> _logger;

    public BinService(BinStore bin, WorkerSignals signals, ProgressTracker progress, ILogger<BinService> logger)
    {
        _bin = bin;
        _signals = signals;
        _progress = progress;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var changed = _bin.Process(SpankBankPaths.Current.MediaDir, DateTimeOffset.UtcNow);
                if (changed > 0)
                {
                    _logger.LogInformation("Rubbish bin: {Count} post(s) handled.", changed);
                    _progress.ForgetRecent(); // their previews are gone
                }
                await _signals.Bin.WaitAsync(ExpiryCheckInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Rubbish bin pass failed; retrying in a minute.");
                try { await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); }
                catch (OperationCanceledException) { return; }
            }
        }
    }
}
