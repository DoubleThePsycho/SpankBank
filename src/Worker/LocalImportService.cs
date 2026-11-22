using SpankBank.Data;
using SpankBank.Data.Downloads;
using SpankBank.Worker.Progress;

namespace SpankBank.Worker;

/// <summary>
/// Files the uploads the user made for missing posts (see <see cref="LocalImportStore"/>). Sleeps until the web app signals;
/// the hourly wake-up retries uploads that failed (for example while the disk was full).
/// </summary>
public sealed class LocalImportService : BackgroundService
{
    private static readonly TimeSpan RetryInterval = TimeSpan.FromHours(1);

    private readonly LocalImportStore _imports;
    private readonly WorkerSignals _signals;
    private readonly ProgressTracker _progress;
    private readonly ILogger<LocalImportService> _logger;

    public LocalImportService(LocalImportStore imports, WorkerSignals signals, ProgressTracker progress, ILogger<LocalImportService> logger)
    {
        _imports = imports;
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
                var saved = _imports.Process(SpankBankPaths.Current.MediaDir);
                if (saved > 0)
                {
                    _logger.LogInformation("Imported {Count} uploaded file(s).", saved);
                    _progress.ForgetRecent();
                }
                await _signals.Imports.WaitAsync(RetryInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Importing uploaded files failed; retrying in a minute.");
                try { await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); }
                catch (OperationCanceledException) { return; }
            }
        }
    }
}
