using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using SpankBank.Core;

namespace SpankBank.Data.Downloads;

public enum FetchResult
{
    /// <summary>The original, from e621.</summary>
    Downloaded,
    /// <summary>The original, recovered from the Wayback Machine (md5 verified).</summary>
    Recovered,
    /// <summary>Only e621's sample was found in the Wayback Machine.</summary>
    SampleOnly,
    /// <summary>The post is gone from e621 and nothing was found in the archive.</summary>
    NotAvailable,
    /// <summary>A network or file problem; worth trying again later.</summary>
    Failed,
    /// <summary>The Wayback Machine is rate limiting us; try again at <see cref="FetchOutcome.RetryAt"/> without counting it as a failure.</summary>
    Deferred,
}

public sealed record FetchOutcome(FetchResult Result, string Detail, DateTimeOffset? RetryAt = null);

/// <summary>
/// Gets a post's file onto disk. Active posts come from e621's CDN. Deleted posts (or ones the CDN no longer has)
/// are looked for in the Wayback Machine: first e621's own file for that md5 (verified), then its sample.
/// </summary>
public sealed partial class PostFetcher : IDisposable
{
    private static string StaticHost => $"https://{SiteConfig.StaticHost}";
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan WaybackStallTimeout = TimeSpan.FromSeconds(120);

    private readonly HttpClient _http;
    private readonly RateGate _wayback = new(TimeSpan.FromSeconds(1));
    // The snapshot index (CDX) allows about 15 lookups a minute per address; going over gets the address firewalled for an hour or more.
    private readonly RateGate _waybackSearch = new(TimeSpan.FromSeconds(5));
    private static readonly TimeSpan MaxWaybackBackoff = TimeSpan.FromHours(1);

    // The Internet Archive answers 429 when it is asked too much; stay away from it for a while (1 minute, doubling to 15).
    private DateTimeOffset _waybackBlockedUntil = DateTimeOffset.MinValue;
    private TimeSpan _waybackBackoff = TimeSpan.FromMinutes(1);

    /// <summary>Until when the Wayback Machine is left alone; the downloader holds back deleted posts until then.</summary>
    public DateTimeOffset WaybackBlockedUntil => _waybackBlockedUntil;

    public PostFetcher()
    {
        // Idle keep-alive connections are dropped quickly: after a long idle spell a NAT or firewall may have forgotten them, and a
        // request sent down such a connection waits (for an hour or more) instead of failing. Connecting has its own limit.
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(30),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        };
        _http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("SpankBank/0.1 (personal archiver)");
    }

    public async Task<FetchOutcome> FetchAsync(CatalogPost post, string mediaRoot, CancellationToken cancellationToken, Action<long, long?>? onProgress = null, Action<string>? onStage = null)
    {
        if (post.Md5.Length != 32 || post.Ext.Length == 0)
            return new FetchOutcome(FetchResult.NotAvailable, "The catalog has no file for this post.");

        var original = MediaPaths.Original(mediaRoot, post.Md5, post.Ext);
        if (File.Exists(original) && await Md5OfAsync(original, cancellationToken) == post.Md5)
            return new FetchOutcome(FetchResult.Downloaded, "Already on disk.");

        // Try the CDN even for posts the export marks deleted: they may have been restored since the export was made.
        onStage?.Invoke($"Downloading from {SiteConfig.Name}");
        {
            var download = await DownloadAsync($"{StaticHost}/data/{post.Md5[..2]}/{post.Md5[2..4]}/{post.Md5}.{post.Ext}",
                original, post.Md5, jpegOnly: false, StallTimeout, cancellationToken, onProgress: onProgress);
            switch (download.Kind)
            {
                case DownloadKind.Ok:
                    await TryDownloadPreviewAsync(post, mediaRoot, cancellationToken);
                    return new FetchOutcome(FetchResult.Downloaded, $"Downloaded from {SiteConfig.Name}.");
                case DownloadKind.Failed:
                    return new FetchOutcome(FetchResult.Failed, download.Detail);
                // NotFound: deleted since the export was made; look in the archive.
            }
        }

        onStage?.Invoke($"Not on {SiteConfig.Name}; trying its sources");
        if (await RecoverFromSourcesAsync(post, original, cancellationToken, onProgress) is { } fromSource)
            return fromSource;

        onStage?.Invoke("Not at its sources; looking in the Wayback Machine");
        return await RecoverAsync(post, mediaRoot, original, cancellationToken, onProgress);
    }

    // ---------------------------------------------------------------- Wayback Machine

    private async Task<FetchOutcome> RecoverAsync(CatalogPost post, string mediaRoot, string original, CancellationToken cancellationToken, Action<long, long?>? onProgress)
    {
        if (DateTimeOffset.UtcNow < _waybackBlockedUntil)
            return Deferred();

        var path = $"data/{post.Md5[..2]}/{post.Md5[2..4]}/{post.Md5}.{post.Ext}";
        // e621's own addresses, then the file addresses its source list gives (the archive may have kept those).
        var addresses = new[] { $"{SiteConfig.StaticHost}/{path}", $"{SiteConfig.Host}/{path}" }
            .Concat(SourceLinks.DirectFileUrls(post.Source).Take(3).Select(SourceLinks.WithoutScheme));
        foreach (var address in addresses)
        {
            var snapshots = await SnapshotsAsync(address, 6, cancellationToken);
            if (snapshots is null)
                return Deferred();

            foreach (var snapshot in snapshots)
            {
                var result = await DownloadAsync(ReplayUrl(snapshot), original, post.Md5, jpegOnly: false, WaybackStallTimeout, cancellationToken, _wayback, onProgress);
                if (result.Kind == DownloadKind.Ok)
                    return new FetchOutcome(FetchResult.Recovered, $"Original recovered from the Wayback Machine ({snapshot.Timestamp[..8]}).");
            }
        }

        var samples = await SnapshotsAsync($"{SiteConfig.StaticHost}/data/sample/{post.Md5[..2]}/{post.Md5[2..4]}/{post.Md5}.jpg", 4, cancellationToken);
        if (samples is null)
            return Deferred();

        var samplePath = MediaPaths.Sample(mediaRoot, post.Md5);
        foreach (var snapshot in samples)
        {
            var result = await DownloadAsync(ReplayUrl(snapshot), samplePath, expectedMd5: null, jpegOnly: true, WaybackStallTimeout, cancellationToken, _wayback);
            if (result.Kind == DownloadKind.Ok)
                return new FetchOutcome(FetchResult.SampleOnly, $"Only the sample was recovered from the Wayback Machine ({snapshot.Timestamp[..8]}).");
        }

        return new FetchOutcome(FetchResult.NotAvailable, $"Deleted on {SiteConfig.Name} and not found at its sources or in the Wayback Machine.");
    }

    private FetchOutcome Deferred()
        => new(FetchResult.Deferred, "The Wayback Machine is limiting requests; trying again later.", _waybackBlockedUntil > DateTimeOffset.UtcNow ? _waybackBlockedUntil : DateTimeOffset.UtcNow.AddMinutes(1));

    private void BackOffWayback()
    {
        _waybackBackoff = TimeSpan.FromSeconds(Math.Min(_waybackBackoff.TotalSeconds * 2, MaxWaybackBackoff.TotalSeconds));
        _waybackBlockedUntil = DateTimeOffset.UtcNow + _waybackBackoff;
    }

    private void WaybackSaysSlowDown(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter?.Delta;
        _waybackBackoff = retryAfter is { } delta && delta > TimeSpan.Zero
            ? TimeSpan.FromSeconds(Math.Min(delta.TotalSeconds, MaxWaybackBackoff.TotalSeconds))
            : TimeSpan.FromSeconds(Math.Min(_waybackBackoff.TotalSeconds * 2, MaxWaybackBackoff.TotalSeconds));
        _waybackBlockedUntil = DateTimeOffset.UtcNow + _waybackBackoff;
    }

    private sealed record Snapshot(string Timestamp, string Original);

    private static string ReplayUrl(Snapshot snapshot) => $"https://web.archive.org/web/{snapshot.Timestamp}id_/{snapshot.Original}";

    /// <summary>Successful snapshots of exactly this address, newest first; null when the index does not answer.</summary>
    private async Task<List<Snapshot>?> SnapshotsAsync(string address, int limit, CancellationToken cancellationToken)
    {
        var url = "https://web.archive.org/cdx/search/cdx?output=json&fl=timestamp,original,statuscode&filter=statuscode:200"
            + $"&limit=-{limit}&url={Uri.EscapeDataString(address)}";
        try
        {
            await _waybackSearch.WaitAsync(cancellationToken);
            // Another lookup may have been refused while this one waited its turn.
            if (DateTimeOffset.UtcNow < _waybackBlockedUntil) return null;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(60));
            using var response = await _http.GetAsync(url, cts.Token);
            if (response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500)
                WaybackSaysSlowDown(response);
            if (!response.IsSuccessStatusCode) return null;
            _waybackBackoff = TimeSpan.FromMinutes(1);

            using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cts.Token), cancellationToken: cts.Token);
            var snapshots = json.RootElement.EnumerateArray().Skip(1)
                .Select(row => new Snapshot(row[0].GetString() ?? "", row[1].GetString() ?? ""))
                .Where(s => s.Timestamp.Length >= 8 && s.Original.Length > 0)
                .OrderByDescending(s => s.Timestamp)
                .ToList();
            return snapshots;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            // Not answering at all (refused, timed out) usually means this address is being kept out: back off, don't hammer it.
            if (ex is not JsonException) BackOffWayback();
            return null;
        }
    }

    // ---------------------------------------------------------------- downloading

    private enum DownloadKind { Ok, NotFound, Failed }

    private readonly record struct DownloadResult(DownloadKind Kind, string Detail);

    private async Task<DownloadResult> DownloadAsync(
        string url, string destination, string? expectedMd5, bool jpegOnly, TimeSpan stall,
        CancellationToken cancellationToken, RateGate? gate = null, Action<long, long?>? onProgress = null)
    {
        var part = destination + ".part";
        try
        {
            if (gate is not null) await gate.WaitAsync(cancellationToken);
            // The server has to start answering within the stall time; the body is guarded per read below.
            using var headerTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            headerTimeout.CancelAfter(stall);
            using var response = await _http.SendAsync(new HttpRequestMessage(HttpMethod.Get, url), HttpCompletionOption.ResponseHeadersRead, headerTimeout.Token);
            // If the site closes it may redirect elsewhere: a request for the site that ends anywhere else is refused (and retried
            // later), never taken as an answer, so another site's 404s or files cannot mark posts lost or fill the media folder.
            if (SiteConfig.IsSiteHost(new Uri(url).Host) && response.RequestMessage?.RequestUri is { } final && !SiteConfig.IsSiteHost(final.Host))
                return new DownloadResult(DownloadKind.Failed, $"{SiteConfig.Name} redirected to {final.Host}; ignored.");
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden or HttpStatusCode.Gone)
                return new DownloadResult(DownloadKind.NotFound, $"HTTP {(int)response.StatusCode}");
            if (!response.IsSuccessStatusCode)
                return new DownloadResult(DownloadKind.Failed, $"HTTP {(int)response.StatusCode} from {new Uri(url).Host}");

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
            var size = 0L;
            var head = new byte[2];
            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var target = File.Create(part))
            {
                var buffer = new byte[81920];
                while (true)
                {
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    cts.CancelAfter(stall);
                    var read = await source.ReadAsync(buffer, cts.Token);
                    if (read == 0) break;
                    if (size < 2) Array.Copy(buffer, 0, head, (int)size, (int)Math.Min(read, 2 - size));
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    hash.AppendData(buffer, 0, read);
                    size += read;
                    onProgress?.Invoke(size, response.Content.Headers.ContentLength);
                }
            }

            var good = expectedMd5 is not null
                ? Convert.ToHexStringLower(hash.GetHashAndReset()) == expectedMd5
                : jpegOnly && size > 2000 && head[0] == 0xFF && head[1] == 0xD8;
            if (!good)
            {
                File.Delete(part);
                // From the archive a wrong file just means this snapshot is no use; from e621 it is worth a retry.
                return new DownloadResult(gate is null ? DownloadKind.Failed : DownloadKind.NotFound, "The file did not match its md5.");
            }

            File.Move(part, destination, overwrite: true);
            return new DownloadResult(DownloadKind.Ok, "");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            TryDelete(part);
            return new DownloadResult(DownloadKind.Failed, ex is OperationCanceledException ? "The transfer stalled or the server did not answer in time." : ex.Message);
        }
    }

    private async Task TryDownloadPreviewAsync(CatalogPost post, string mediaRoot, CancellationToken cancellationToken)
    {
        var preview = MediaPaths.Preview(mediaRoot, post.Md5);
        if (File.Exists(preview)) return;

        var url = $"{StaticHost}/data/preview/{post.Md5[..2]}/{post.Md5[2..4]}/{post.Md5}.jpg";
        // The preview is a derived file with its own md5, so it is only checked for being a JPEG.
        await DownloadAsync(url, preview, expectedMd5: null, jpegOnly: true, StallTimeout, cancellationToken);
    }

    private static async Task<string> Md5OfAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(await MD5.HashDataAsync(stream, cancellationToken));
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { }
    }

    public void Dispose() => _http.Dispose();

    /// <summary>At most one call per interval, shared by every thread that uses it.</summary>
    private sealed class RateGate(TimeSpan interval)
    {
        private readonly object _lock = new();
        private DateTimeOffset _next = DateTimeOffset.MinValue;

        public async Task WaitAsync(CancellationToken cancellationToken)
        {
            TimeSpan delay;
            lock (_lock)
            {
                var now = DateTimeOffset.UtcNow;
                var start = _next > now ? _next : now;
                _next = start + interval;
                delay = start - now;
            }
            if (delay > TimeSpan.Zero) await Task.Delay(delay, cancellationToken);
        }
    }
}
