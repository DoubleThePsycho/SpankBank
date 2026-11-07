using SpankBank.Core;
using Microsoft.Data.Sqlite;
using SpankBank.Data;
using SpankBank.Data.Import;
using SpankBank.Data.Matching;
using SpankBank.Data.Repositories;
using SpankBank.Worker.Progress;

namespace SpankBank.Worker;

/// <summary>Downloads the latest e621 CSV exports, imports tags, aliases, pools and posts, then re-matches the watches.</summary>
public sealed class ExportRefreshJob
{
    private static string PhaseDownload => $"Download the {SiteConfig.Name} exports";
    private const string PhaseTags = "Import tags";
    private const string PhaseAliases = "Import tag aliases";
    private const string PhasePools = "Import pools";
    private const string PhasePosts = "Import posts";
    private const string PhaseMatch = "Match your watches";

    private readonly DatabasePaths _paths;
    private readonly string _importsDir;
    private readonly ILogger<ExportRefreshJob> _logger;
    private readonly ProgressTracker _progress;
    private readonly SettingsRepository _settings;
    private readonly ImportHistoryRepository _history;
    private readonly E621ExportDownloadService _downloader = new();
    private readonly E621ExportImportService _importer;
    private readonly WatchMatcher _matcher;

    public ExportRefreshJob(DatabasePaths paths, string importsDir, ProgressTracker progress, ILogger<ExportRefreshJob> logger)
    {
        _paths = paths;
        _importsDir = importsDir;
        _progress = progress;
        _logger = logger;
        _settings = new SettingsRepository(paths.AppDatabasePath);
        _history = new ImportHistoryRepository(paths.AppDatabasePath);
        _importer = new E621ExportImportService(paths.ServerDatabasePath, paths.AppDatabasePath);
        _matcher = new WatchMatcher(paths, message => logger.LogInformation("{Message}", message));
    }

    /// <returns>Whether the refresh succeeded and a short summary; throws only on cancellation.</returns>
    public async Task<RefreshOutcome> RunAsync(CancellationToken cancellationToken)
    {
        _progress.BeginRun($"Updating from {SiteConfig.Name}", PhaseDownload, PhaseTags, PhaseAliases, PhasePools, PhasePosts, PhaseMatch);
        var result = await RunPhasesAsync(cancellationToken);
        _progress.EndRun(result.Text);
        return result;
    }

    private async Task<RefreshOutcome> RunPhasesAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_importsDir);
        _settings.Set(ImportSettingsKeys.LastExportCheckAt, DateTimeOffset.UtcNow.ToString("o"));

        _progress.StartPhase(PhaseDownload);
        var download = await _downloader.DownloadLatestExportsAsync(
            _importsDir,
            progress: new InlineProgress<ExportDownloadProgress>(ReportDownload),
            cancellationToken: cancellationToken,
            exportTypes: E621ExportDownloadService.ImportableExportTypes,
            alreadyImportedFileNames: _history.GetSuccessfullyImportedFileNames(E621ExportDownloadService.ImportableExportTypes));

        if (!download.Succeeded)
        {
            _progress.EndPhase(PhaseDownload, PhaseState.Failed);
            return Failed($"Download failed: {download.ErrorMessage}");
        }

        var missing = download.Items
            .Where(item => item.Status == ExportDownloadStatus.MissingFromIndex)
            .Select(item => item.ExportType)
            .ToList();
        if (missing.Count > 0)
        {
            _progress.EndPhase(PhaseDownload, PhaseState.Failed);
            return Failed($"Export index is missing: {string.Join(", ", missing)}.");
        }

        _progress.EndPhase(PhaseDownload);
        _settings.Set(ImportSettingsKeys.LastExportDownloadAt, DateTimeOffset.UtcNow.ToString("o"));

        var detected = E621ExportFileDetector.DetectLatest(_importsDir);
        var summary = new List<string>();

        // Order matters: tags first, then aliases (which update tags), then pools, then posts. Stop on the first failure.
        if (detected.TagsPath is { } tagsPath)
        {
            _progress.StartPhase(PhaseTags);
            var estimate = CountRows("Tags");
            var result = await _importer.ImportTagsAsync(tagsPath,
                new InlineProgress<TagsImportProgress>(p => _progress.Step("Tags", p.RowsRead, estimate, "rows")), cancellationToken);
            if (!result.History.Succeeded)
            {
                _progress.EndPhase(PhaseTags, PhaseState.Failed);
                return Failed($"Tags import failed: {result.History.ErrorMessage}");
            }
            Cleanup(tagsPath);
            summary.Add($"tags {result.RowsImportedOrUpdated:N0}");
            _progress.EndPhase(PhaseTags);
        }
        else _progress.EndPhase(PhaseTags, PhaseState.Skipped);

        if (detected.TagAliasesPath is { } aliasesPath)
        {
            _progress.StartPhase(PhaseAliases);
            var estimate = CountRows("TagAliases");
            var result = await _importer.ImportTagAliasesAsync(aliasesPath,
                new InlineProgress<TagAliasesImportProgress>(p => _progress.Step("Tag aliases", p.RowsRead, estimate, "rows")), cancellationToken);
            if (!result.History.Succeeded)
            {
                _progress.EndPhase(PhaseAliases, PhaseState.Failed);
                return Failed($"Aliases import failed: {result.History.ErrorMessage}");
            }
            Cleanup(aliasesPath);
            summary.Add($"aliases {result.TagsUpdated:N0}");
            _progress.EndPhase(PhaseAliases);
        }
        else _progress.EndPhase(PhaseAliases, PhaseState.Skipped);

        if (detected.PoolsPath is { } poolsPath)
        {
            _progress.StartPhase(PhasePools);
            _progress.Step("Pools", 0, null, "rows");
            var result = await _importer.ImportPoolsAsync(poolsPath, cancellationToken);
            if (!result.History.Succeeded)
            {
                _progress.EndPhase(PhasePools, PhaseState.Failed);
                return Failed($"Pools import failed: {result.History.ErrorMessage}");
            }
            Cleanup(poolsPath);
            summary.Add($"pools {result.RowsImported:N0}");
            _progress.EndPhase(PhasePools);
        }
        else _progress.EndPhase(PhasePools, PhaseState.Skipped);

        if (detected.PostsPath is { } postsPath)
        {
            _progress.StartPhase(PhasePosts);
            var estimate = CountRows("Posts");
            var result = await _importer.ImportPostsAsync(postsPath,
                new InlineProgress<PostsImportProgress>(p => _progress.Step("Posts", p.RowsRead, estimate, "rows")), cancellationToken);
            if (!result.History.Succeeded)
            {
                _progress.EndPhase(PhasePosts, PhaseState.Failed);
                return Failed($"Posts import failed: {result.History.ErrorMessage}");
            }
            Cleanup(postsPath);
            summary.Add($"posts {result.RowsImported:N0}");
            _progress.EndPhase(PhasePosts);
        }
        else _progress.EndPhase(PhasePosts, PhaseState.Skipped);

        var text = summary.Count == 0 ? "Nothing new to import." : "Imported " + string.Join(", ", summary) + ".";

        _progress.StartPhase(PhaseMatch);
        var matched = await Task.Run(() => _matcher.Run(all: true, cancellationToken,
            (scanned, total) => _progress.Step("Scanning posts", scanned, total, "posts")), cancellationToken);
        _progress.EndPhase(PhaseMatch, matched.WatchesMatched > 0 ? PhaseState.Done : PhaseState.Skipped);
        if (matched.WatchesMatched > 0)
            text += $" Matched {matched.PostsMatched:N0} posts for {matched.WatchesMatched} watches."
                + (matched.PoolsAdded > 0 ? $" Added {matched.PoolsAdded} pools." : "");

        return new RefreshOutcome(true, text);
    }

    private static RefreshOutcome Failed(string text) => new(false, text);

    private void ReportDownload(ExportDownloadProgress progress)
    {
        var name = progress.CurrentFileName ?? "Checking the export index";
        _progress.Step(name, progress.OverallBytesDownloaded, progress.OverallTotalBytes, "bytes");

        if (progress.State == ExportDownloadProgressState.Downloading)
        {
            var now = DateTimeOffset.UtcNow;
            if (now - _lastProgressLog < TimeSpan.FromSeconds(10)) return;
            _lastProgressLog = now;
        }
        _logger.LogInformation("{Message}", progress.Message);
    }

    private DateTimeOffset _lastProgressLog = DateTimeOffset.MinValue;

    private long? CountRows(string table)
    {
        try
        {
            using var connection = new SqliteConnection($"Data Source={_paths.ServerDatabasePath};Mode=ReadOnly");
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = $"SELECT COUNT(*) FROM {table};";
            var count = Convert.ToInt64(cmd.ExecuteScalar());
            return count > 0 ? count : null;
        }
        catch (SqliteException)
        {
            return null;
        }
    }

    private void Cleanup(string path)
    {
        var cleanup = E621ExportFileCleanup.DeleteAfterSuccessfulHandledImport(path, _importsDir);
        if (cleanup.Warning is not null)
            _logger.LogWarning("{Warning}", cleanup.Warning);
    }
}

public sealed record RefreshOutcome(bool Succeeded, string Text);

/// <summary>IProgress that runs the callback on the reporting thread (Progress&lt;T&gt; would queue it).</summary>
internal sealed class InlineProgress<T>(Action<T> callback) : IProgress<T>
{
    public void Report(T value) => callback(value);
}
