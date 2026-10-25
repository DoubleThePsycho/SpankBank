using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

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
    private static readonly Uri ExportIndexUri = new("https://e621.net/db_exports.json");
    public static readonly IReadOnlyList<string> ImportableExportTypes = ["tags", "tag_aliases", "posts"];
    private static readonly string[] ExportTypes =
    [
        "posts",
        "tags",
        "pools",
        "tag_aliases",
        "tag_implications",
    ];

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
                    forceOverwrite: forceRedownload,
                    expectedSha256: exportFile.Sha256);

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
        var indexJson = await _httpClient.GetStringAsync(ExportIndexUri, cancellationToken);
        return ParseLatestExports(indexJson);
    }

    // e621 publishes one undated file per export (e.g. posts.csv.gz) and lists its update time in
    // /db_exports.json. Local files are named <type>-<yyyy-MM-dd>.csv.gz so the detector, import
    // history and cleanup can tell one day's export from the next.
    private static IReadOnlyDictionary<string, ExportIndexFile> ParseLatestExports(string indexJson)
    {
        var files = new Dictionary<string, ExportIndexFile>();
        using var document = JsonDocument.Parse(indexJson);
        foreach (var entry in document.RootElement.EnumerateArray())
        {
            var type = entry.GetProperty("name").GetString();
            if (type is null || !ExportTypes.Contains(type))
                continue;

            var updatedAt = entry.GetProperty("updated_at").GetDateTimeOffset().UtcDateTime;
            var date = DateOnly.FromDateTime(updatedAt);
            files[type] = new ExportIndexFile(
                Type: type,
                FileName: $"{type}-{date:yyyy-MM-dd}.csv.gz",
                Date: date,
                DownloadUri: new Uri(entry.GetProperty("url").GetString()!),
                Sha256: entry.TryGetProperty("checksum", out var checksum) ? checksum.GetString() : null);
        }

        return files;
    }

    private async Task<long> DownloadFileAsync(
        Uri downloadUri,
        string localPath,
        long? currentTotalBytes,
        long completedBytesBeforeFile,
        long? overallTotalBytes,
        IProgress<ExportDownloadProgress>? progress,
        CancellationToken cancellationToken,
        bool forceOverwrite,
        string? expectedSha256 = null)
    {
        var partialPath = localPath + ".partial";
        var fileName = Path.GetFileName(localPath);

        // A leftover .partial from an earlier attempt is resumed with a Range request, not restarted.
        // The checksum below catches a resumed file that does not match the current export.
        const int maxAttempts = 20;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await DownloadChunkAsync(
                    downloadUri, partialPath, fileName, currentTotalBytes,
                    completedBytesBeforeFile, overallTotalBytes, progress, cancellationToken);
                break;
            }
            catch (Exception ex) when (attempt < maxAttempts
                && !cancellationToken.IsCancellationRequested
                && ex is IOException or HttpRequestException or OperationCanceledException)
            {
                progress?.Report(new ExportDownloadProgress(
                    $"{fileName}: transfer interrupted ({ex.Message}); retrying {attempt}/{maxAttempts - 1} and resuming...",
                    fileName,
                    File.Exists(partialPath) ? new FileInfo(partialPath).Length : 0,
                    currentTotalBytes,
                    completedBytesBeforeFile,
                    overallTotalBytes,
                    ExportDownloadProgressState.Downloading));
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(60, 5 * attempt)), cancellationToken);
            }
        }

        if (!string.IsNullOrWhiteSpace(expectedSha256))
        {
            string actual;
            await using (var verifyStream = File.OpenRead(partialPath))
                actual = Convert.ToHexString(await SHA256.HashDataAsync(verifyStream, cancellationToken));

            if (!actual.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(partialPath);
                throw new InvalidOperationException($"{fileName}: checksum mismatch (expected {expectedSha256}, got {actual}).");
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

    // Downloads (or resumes) into partialPath. Throws if no bytes arrive for StallTimeout.
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(90);

    private async Task DownloadChunkAsync(
        Uri downloadUri,
        string partialPath,
        string fileName,
        long? currentTotalBytes,
        long completedBytesBeforeFile,
        long? overallTotalBytes,
        IProgress<ExportDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var resumeFrom = File.Exists(partialPath) ? new FileInfo(partialPath).Length : 0;

        using var request = new HttpRequestMessage(HttpMethod.Get, downloadUri);
        if (resumeFrom > 0)
            request.Headers.Range = new RangeHeaderValue(resumeFrom, null);

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            // The partial is complete or larger than the file; leave it for the checksum to judge.
            return;
        }

        response.EnsureSuccessStatusCode();

        // 200 means the server ignored the Range header: start over from zero.
        if (resumeFrom > 0 && response.StatusCode != HttpStatusCode.PartialContent)
        {
            resumeFrom = 0;
            File.Delete(partialPath);
        }

        if (currentTotalBytes is null && response.Content.Headers.ContentLength is { } length)
            currentTotalBytes = resumeFrom + length;

        await using var remoteStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var localStream = new FileStream(
            partialPath,
            resumeFrom > 0 ? FileMode.Append : FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 1024 * 128,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        var buffer = new byte[1024 * 128];
        var downloadedBytes = resumeFrom;
        while (true)
        {
            using var stallCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            stallCts.CancelAfter(StallTimeout);

            int read;
            try
            {
                read = await remoteStream.ReadAsync(buffer, stallCts.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new IOException($"no data received for {StallTimeout.TotalSeconds:0} seconds");
            }

            if (read == 0)
                break;

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
        Uri DownloadUri,
        string? Sha256
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
