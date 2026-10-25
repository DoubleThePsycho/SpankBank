using SpankBank.Data;
using SpankBank.Data.Import;
using SpankBank.Data.Repositories;

namespace SpankBank.Worker;

/// <summary>Downloads the latest e621 CSV exports and imports tags, aliases and posts.</summary>
public sealed class ExportRefreshJob
{
    private readonly DatabasePaths _paths;
    private readonly string _importsDir;
    private readonly ILogger<ExportRefreshJob> _logger;
    private readonly SettingsRepository _settings;
    private readonly ImportHistoryRepository _history;
    private readonly E621ExportDownloadService _downloader = new();
    private readonly E621ExportImportService _importer;

    public ExportRefreshJob(DatabasePaths paths, string importsDir, ILogger<ExportRefreshJob> logger)
    {
        _paths = paths;
        _importsDir = importsDir;
        _logger = logger;
        _settings = new SettingsRepository(paths.AppDatabasePath);
        _history = new ImportHistoryRepository(paths.AppDatabasePath);
        _importer = new E621ExportImportService(paths.ServerDatabasePath, paths.AppDatabasePath);
    }

    /// <returns>A short result summary; throws only on cancellation.</returns>
    public async Task<string> RunAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_importsDir);
        _settings.Set(ImportSettingsKeys.LastExportCheckAt, DateTimeOffset.UtcNow.ToString("o"));

        var download = await _downloader.DownloadLatestExportsAsync(
            _importsDir,
            progress: new Progress<ExportDownloadProgress>(p => _logger.LogInformation("{Message}", p.Message)),
            cancellationToken: cancellationToken,
            exportTypes: E621ExportDownloadService.ImportableExportTypes,
            alreadyImportedFileNames: _history.GetSuccessfullyImportedFileNames(E621ExportDownloadService.ImportableExportTypes));

        if (!download.Succeeded)
            return $"Download failed: {download.ErrorMessage}";

        _settings.Set(ImportSettingsKeys.LastExportDownloadAt, DateTimeOffset.UtcNow.ToString("o"));

        var detected = E621ExportFileDetector.DetectLatest(_importsDir);
        var summary = new List<string>();

        // Order matters: tags first, then aliases (which update tags), then posts. Stop on the first failure.
        if (detected.TagsPath is { } tagsPath)
        {
            var result = await _importer.ImportTagsAsync(tagsPath, cancellationToken: cancellationToken);
            if (!result.History.Succeeded)
                return $"Tags import failed: {result.History.ErrorMessage}";
            Cleanup(tagsPath);
            summary.Add($"tags {result.RowsImportedOrUpdated:N0}");
        }

        if (detected.TagAliasesPath is { } aliasesPath)
        {
            var result = await _importer.ImportTagAliasesAsync(aliasesPath, cancellationToken: cancellationToken);
            if (!result.History.Succeeded)
                return $"Aliases import failed: {result.History.ErrorMessage}";
            Cleanup(aliasesPath);
            summary.Add($"aliases {result.TagsUpdated:N0}");
        }

        if (detected.PostsPath is { } postsPath)
        {
            var result = await _importer.ImportPostsAsync(postsPath, cancellationToken: cancellationToken);
            if (!result.History.Succeeded)
                return $"Posts import failed: {result.History.ErrorMessage}";
            Cleanup(postsPath);
            summary.Add($"posts {result.RowsImported:N0}");
        }

        return summary.Count == 0 ? "Nothing new to import." : "Imported " + string.Join(", ", summary) + ".";
    }

    private void Cleanup(string path)
    {
        var cleanup = E621ExportFileCleanup.DeleteAfterSuccessfulHandledImport(path, _importsDir);
        if (cleanup.Warning is not null)
            _logger.LogWarning("{Warning}", cleanup.Warning);
    }
}
