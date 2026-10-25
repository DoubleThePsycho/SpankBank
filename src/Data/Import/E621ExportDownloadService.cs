using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;

namespace SpankBank.Data.Import;

public sealed record ExportDownloadResult(
    IReadOnlyList<ExportDownloadItemResult> Items,
    bool Succeeded,
    string? ErrorMessage
)
{
    public int PresentCount
        => Items.Count(item => item.Status is ExportDownloadStatus.Downloaded or ExportDownloadStatus.AlreadyExists);

    public int ExpectedCount
        => Items.Count(item => item.Status != ExportDownloadStatus.MissingFromIndex || item.ExportType != "tag_implications");

    public bool UpToDate
        => Succeeded && Items.Count > 0 && Items.All(item =>
            item.Status is ExportDownloadStatus.AlreadyExists or ExportDownloadStatus.MissingFromIndex);

    public bool DownloadedAny
        => Items.Any(item => item.Status == ExportDownloadStatus.Downloaded);
}

public sealed record ExportLatestCheckResult(
    IReadOnlyList<ExportLatestCheckItem> Items,
    bool Succeeded,
    string? ErrorMessage
)
{
    public bool HasMissingRequiredExports
        => Items.Any(item => !item.Optional && !item.ExistsLocally);

    public bool HasMissingExports
        => Items.Any(item => !item.ExistsLocally);
}

public sealed record ExportLatestCheckItem(
    string ExportType,
    string? FileName,
    string? LocalPath,
    bool ExistsLocally,
    bool Optional,
    string Message
);

public sealed record ExportDownloadItemResult(
    string ExportType,
    string? FileName,
    string? LocalPath,
    ExportDownloadStatus Status,
    string Message
);

public sealed record ExportDownloadProgress(
    string Message,
    string? CurrentFileName,
    long CurrentBytesDownloaded,
    long? CurrentTotalBytes,
    long OverallBytesDownloaded,
    long? OverallTotalBytes,
    ExportDownloadProgressState State
)
{
    public bool HasKnownCurrentTotal => CurrentTotalBytes.HasValue && CurrentTotalBytes.Value > 0;

    public bool HasKnownOverallTotal => OverallTotalBytes.HasValue && OverallTotalBytes.Value > 0;
}

public enum ExportDownloadProgressState
{
    CheckingIndex,
    Downloading,
    AlreadyExists,
    UpToDate,
    DownloadComplete,
    Failed,
    MissingFromIndex,
}

public enum ExportDownloadStatus
{
    Downloaded,
    AlreadyExists,
    MissingFromIndex,
    Failed,
}

public sealed class E621ExportDownloadService
{
    private static readonly Uri ExportIndexUri = new("https://e621.net/db_export/");
    public static readonly IReadOnlyList<string> ImportableExportTypes = ["tags", "tag_aliases", "posts"];
    private static readonly string[] ExportTypes =
    [
        "posts",
        "tags",
        "pools",
        "tag_aliases",
        "tag_implications",
    ];

    private static readonly Regex ExportFileRegex = new(
        @"(?<file>(?<type>posts|tags|pools|tag_aliases|tag_implications)-(?<date>\d{4}-\d{2}-\d{2})\.csv\.gz)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly HttpClient _httpClient;

    public E621ExportDownloadService()
    {
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("SpankBank-CSharp-local-export-importer", "0.1"));
    }

    public async Task<ExportDownloadResult> DownloadLatestExportsAsync(
        string exportFolderPath,
        IProgress<ExportDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default,
        IReadOnlyCollection<string>? exportTypes = null,
        IReadOnlyCollection<string>? alreadyImportedFileNames = null,
        bool forceRedownload = false)
    {
        var results = new List<ExportDownloadItemResult>();

        try
        {
            if (string.IsNullOrWhiteSpace(exportFolderPath))
                throw new InvalidOperationException("Export folder path is required.");

            Directory.CreateDirectory(exportFolderPath);

            progress?.Report(new ExportDownloadProgress(
                "Checking E621 export index...",
                null,
                0,
                null,
                0,
                null,
                ExportDownloadProgressState.CheckingIndex));

            var requestedTypes = NormalizeExportTypes(exportTypes);
            var imported = new HashSet<string>(alreadyImportedFileNames ?? [], StringComparer.OrdinalIgnoreCase);
            var latest = await GetLatestExportIndexAsync(cancellationToken);
            var missingFiles = requestedTypes
                .Where(type => latest.ContainsKey(type))
                .Select(type => latest[type])
                .Where(file => forceRedownload
                    || (!File.Exists(Path.Combine(exportFolderPath, file.FileName)) && !imported.Contains(file.FileName)))
                .ToList();

            var knownTotals = new Dictionary<string, long>();
            foreach (var file in missingFiles)
            {
                var total = await TryGetContentLengthAsync(file.DownloadUri, cancellationToken);
                if (total.HasValue)
                    knownTotals[file.FileName] = total.Value;
            }

            long? overallTotal = knownTotals.Count == missingFiles.Count
                ? knownTotals.Values.Sum()
                : null;
            long completedBytes = 0;

            foreach (var exportType in requestedTypes)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!latest.TryGetValue(exportType, out var exportFile))
                {
                    var message = exportType == "tag_implications"
                        ? "Optional export was not found in the index."
                        : "Export was not found in the index.";
                    progress?.Report(new ExportDownloadProgress(
                        $"{exportType}: {message}",
                        null,
                        0,
                        null,
                        completedBytes,
                        overallTotal,
                        ExportDownloadProgressState.MissingFromIndex));
                    results.Add(new ExportDownloadItemResult(
                        exportType,
                        FileName: null,
                        LocalPath: null,
                        ExportDownloadStatus.MissingFromIndex,
                        message));
                    continue;
                }

                var localPath = Path.Combine(exportFolderPath, exportFile.FileName);
                if (!forceRedownload && imported.Contains(exportFile.FileName))
                {
                    progress?.Report(new ExportDownloadProgress(
                        $"{exportType}: already imported ({exportFile.FileName}).",
                        exportFile.FileName,
                        0,
                        null,
                        completedBytes,
                        overallTotal,
                        ExportDownloadProgressState.AlreadyExists));
                    results.Add(new ExportDownloadItemResult(
                        exportType,
                        exportFile.FileName,
                        localPath,
                        ExportDownloadStatus.AlreadyExists,
                        "Already imported."));
                    continue;
                }

                if (!forceRedownload && File.Exists(localPath))
                {
                    progress?.Report(new ExportDownloadProgress(
                        $"{exportType}: already exists ({exportFile.FileName}).",
                        exportFile.FileName,
                        0,
                        null,
                        completedBytes,
                        overallTotal,
                        ExportDownloadProgressState.AlreadyExists));
                    results.Add(new ExportDownloadItemResult(
                        exportType,
                        exportFile.FileName,
                        localPath,
                        ExportDownloadStatus.AlreadyExists,
                        "Already exists locally."));
                    continue;
                }

                progress?.Report(new ExportDownloadProgress(
                    $"{exportType}: downloading {exportFile.FileName}...",
                    exportFile.FileName,
                    0,
                    GetKnownTotal(knownTotals, exportFile.FileName),
                    completedBytes,
                    overallTotal,
                    ExportDownloadProgressState.Downloading));

                var downloadedBytes = await DownloadFileAsync(
                    exportFile.DownloadUri,
                    localPath,
                    GetKnownTotal(knownTotals, exportFile.FileName),
                    completedBytes,
                    overallTotal,
                    progress,
                    cancellationToken,
                    forceOverwrite: forceRedownload);

                completedBytes += downloadedBytes;
                progress?.Report(new ExportDownloadProgress(
                    $"{exportType}: download complete.",
                    exportFile.FileName,
                    downloadedBytes,
                    GetKnownTotal(knownTotals, exportFile.FileName),
                    completedBytes,
                    overallTotal,
                    ExportDownloadProgressState.DownloadComplete));
                results.Add(new ExportDownloadItemResult(
                    exportType,
                    exportFile.FileName,
                    localPath,
                    ExportDownloadStatus.Downloaded,
                    "Download complete."));
            }

            if (results.Count > 0 && results.All(item =>
                    item.Status is ExportDownloadStatus.AlreadyExists or ExportDownloadStatus.MissingFromIndex))
            {
                var present = results.Count(item => item.Status == ExportDownloadStatus.AlreadyExists);
                progress?.Report(new ExportDownloadProgress(
                    $"Latest exports already downloaded. {present} / {results.Count} export files present.",
                    null,
                    0,
                    null,
                    completedBytes,
                    overallTotal,
                    ExportDownloadProgressState.UpToDate));
            }

            return new ExportDownloadResult(results, Succeeded: true, ErrorMessage: null);
        }
        catch (Exception ex)
        {
            progress?.Report(new ExportDownloadProgress(
                $"Export download failed: {ex.Message}",
                null,
                0,
                null,
                0,
                null,
                ExportDownloadProgressState.Failed));
            return new ExportDownloadResult(results, Succeeded: false, ErrorMessage: ex.Message);
        }
    }

    public async Task<ExportLatestCheckResult> CheckLatestExportsAsync(
        string exportFolderPath,
        CancellationToken cancellationToken = default,
        IReadOnlyCollection<string>? exportTypes = null,
        IReadOnlyCollection<string>? alreadyImportedFileNames = null)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(exportFolderPath))
                return new ExportLatestCheckResult([], Succeeded: false, "Export folder path is required.");

            var requestedTypes = NormalizeExportTypes(exportTypes);
            var imported = new HashSet<string>(alreadyImportedFileNames ?? [], StringComparer.OrdinalIgnoreCase);
            var latest = await GetLatestExportIndexAsync(cancellationToken);
            var results = new List<ExportLatestCheckItem>();
            foreach (var exportType in requestedTypes)
            {
                var optional = exportType == "tag_implications";
                if (!latest.TryGetValue(exportType, out var exportFile))
                {
                    results.Add(new ExportLatestCheckItem(
                        exportType,
                        FileName: null,
                        LocalPath: null,
                        ExistsLocally: optional,
                        Optional: optional,
                        Message: optional
                            ? "Optional export was not found in the index."
                            : "Export was not found in the index."));
                    continue;
                }

                var localPath = Path.Combine(exportFolderPath, exportFile.FileName);
                var exists = File.Exists(localPath) || imported.Contains(exportFile.FileName);
                results.Add(new ExportLatestCheckItem(
                    exportType,
                    exportFile.FileName,
                    localPath,
                    exists,
                    optional,
                    exists
                        ? imported.Contains(exportFile.FileName) ? "Already imported." : "Already exists locally."
                        : "Missing locally."));
            }

            return new ExportLatestCheckResult(results, Succeeded: true, ErrorMessage: null);
        }
        catch (Exception ex)
        {
            return new ExportLatestCheckResult([], Succeeded: false, ErrorMessage: ex.Message);
        }
    }

    private async Task<IReadOnlyDictionary<string, ExportIndexFile>> GetLatestExportIndexAsync(
        CancellationToken cancellationToken)
    {
        var indexHtml = await _httpClient.GetStringAsync(ExportIndexUri, cancellationToken);
        return ParseLatestExports(indexHtml);
    }

    private static IReadOnlyDictionary<string, ExportIndexFile> ParseLatestExports(string indexHtml)
        => ExportFileRegex
            .Matches(indexHtml)
            .Select(match => new ExportIndexFile(
                Type: match.Groups["type"].Value.ToLowerInvariant(),
                FileName: WebUtility.HtmlDecode(match.Groups["file"].Value),
                Date: DateOnly.Parse(match.Groups["date"].Value),
                DownloadUri: new Uri(ExportIndexUri, WebUtility.HtmlDecode(match.Groups["file"].Value))))
            .GroupBy(file => file.Type)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderByDescending(file => file.Date)
                    .ThenByDescending(file => file.FileName, StringComparer.OrdinalIgnoreCase)
                    .First());

    private async Task<long> DownloadFileAsync(
        Uri downloadUri,
        string localPath,
        long? currentTotalBytes,
        long completedBytesBeforeFile,
        long? overallTotalBytes,
        IProgress<ExportDownloadProgress>? progress,
        CancellationToken cancellationToken,
        bool forceOverwrite)
    {
        var partialPath = localPath + ".partial";
        if (File.Exists(partialPath))
            File.Delete(partialPath);

        using var response = await _httpClient.GetAsync(
            downloadUri,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        currentTotalBytes ??= response.Content.Headers.ContentLength;
        var fileName = Path.GetFileName(localPath);

        await using (var remoteStream = await response.Content.ReadAsStreamAsync(cancellationToken))
        await using (var localStream = new FileStream(
            partialPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 1024 * 128,
            FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            var buffer = new byte[1024 * 128];
            long downloadedBytes = 0;
            int read;
            while ((read = await remoteStream.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await localStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                downloadedBytes += read;
                progress?.Report(new ExportDownloadProgress(
                    $"{fileName}: {FormatSize(downloadedBytes)} / {(currentTotalBytes.HasValue ? FormatSize(currentTotalBytes.Value) : "total unknown")}",
                    fileName,
                    downloadedBytes,
                    currentTotalBytes,
                    completedBytesBeforeFile + downloadedBytes,
                    overallTotalBytes,
                    ExportDownloadProgressState.Downloading));
            }
        }

        if (File.Exists(localPath))
        {
            if (!forceOverwrite)
            {
                File.Delete(partialPath);
                return 0;
            }

            File.Delete(localPath);
        }

        File.Move(partialPath, localPath);
        return new FileInfo(localPath).Length;
    }

    private async Task<long?> TryGetContentLengthAsync(Uri downloadUri, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, downloadUri);
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return null;

            return response.Content.Headers.ContentLength;
        }
        catch
        {
            return null;
        }
    }

    public static string FormatSize(long bytes)
    {
        if (bytes >= 1_073_741_824)
            return $"{bytes / 1_073_741_824.0:0.##} GB";
        if (bytes >= 1_048_576)
            return $"{bytes / 1_048_576.0:0.##} MB";
        if (bytes >= 1024)
            return $"{bytes / 1024.0:0.##} KB";

        return $"{bytes} B";
    }

    private sealed record ExportIndexFile(
        string Type,
        string FileName,
        DateOnly Date,
        Uri DownloadUri
            );

    private static long? GetKnownTotal(IReadOnlyDictionary<string, long> totals, string fileName)
        => totals.TryGetValue(fileName, out var total) ? total : null;

    private static IReadOnlyList<string> NormalizeExportTypes(IReadOnlyCollection<string>? exportTypes)
        => exportTypes is { Count: > 0 }
            ? exportTypes
                .Select(type => type.ToLowerInvariant())
                .Where(type => ExportTypes.Contains(type, StringComparer.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray()
            : ExportTypes;
}
