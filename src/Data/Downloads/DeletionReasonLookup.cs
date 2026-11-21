using System.Net;
using SpankBank.Core;

namespace SpankBank.Data.Downloads;

/// <summary>
/// Asks the site why posts were deleted: one request for a few dozen posts, at most one a second, with a back-off when the
/// site says it is too much. The reasons are only needed for the "skip inferior versions and low-effort edits" setting.
/// </summary>
public sealed class DeletionReasonLookup : IDisposable
{
    public const int PostsPerRequest = 50;
    private static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(15);

    private readonly HttpClient _http;
    private DateTimeOffset _next = DateTimeOffset.MinValue;
    private DateTimeOffset _blockedUntil = DateTimeOffset.MinValue;
    private TimeSpan _backoff = TimeSpan.FromMinutes(1);

    public DeletionReasonLookup()
    {
        var handler = new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(30), PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30) };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("SpankBank/0.1 (personal archiver)");
    }

    /// <summary>Called with a short message when the site could not be asked (shown in the worker log).</summary>
    public Action<string>? Notice { get; set; }

    /// <summary>The site asked us to slow down: no requests until then.</summary>
    public DateTimeOffset BlockedUntil => _blockedUntil;

    /// <returns>The latest deletion reason of every post asked about (null for none), or null when the site could not be asked.</returns>
    public async Task<IReadOnlyDictionary<long, string?>?> LookUpAsync(IReadOnlyList<long> postIds, CancellationToken cancellationToken)
    {
        if (postIds.Count == 0) return new Dictionary<long, string?>();
        if (DateTimeOffset.UtcNow < _blockedUntil) return null;

        var wait = _next - DateTimeOffset.UtcNow;
        if (wait > TimeSpan.Zero) await Task.Delay(wait, cancellationToken);
        _next = DateTimeOffset.UtcNow + MinInterval;

        var url = SiteConfig.Url($"post_flags.json?search%5Bpost_id%5D={string.Join(',', postIds)}&limit=320");
        try
        {
            using var response = await _http.GetAsync(url, cancellationToken);
            // A site that closed and redirects elsewhere must not answer for this one.
            if (response.RequestMessage?.RequestUri is { } final && !SiteConfig.IsSiteHost(final.Host))
            {
                Notice?.Invoke($"Deletion reasons: {SiteConfig.Name} redirected to {final.Host}; ignored.");
                return Block();
            }
            if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
            {
                Notice?.Invoke($"Deletion reasons: {SiteConfig.Name} answered HTTP {(int)response.StatusCode}; waiting {_backoff.TotalMinutes:0} minute(s).");
                return Block();
            }
            if (!response.IsSuccessStatusCode)
            {
                Notice?.Invoke($"Deletion reasons: HTTP {(int)response.StatusCode} from {SiteConfig.Name}.");
                return Block();
            }

            var result = DeletionReasons.Parse(await response.Content.ReadAsStringAsync(cancellationToken), postIds);
            if (result is null)
            {
                Notice?.Invoke("Deletion reasons: the answer was not a list of flags.");
                return Block();
            }
            _backoff = TimeSpan.FromMinutes(1);
            return result;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            Notice?.Invoke($"Deletion reasons: {ex.Message}");
            return Block();
        }
    }

    private IReadOnlyDictionary<long, string?>? Block()
    {
        _blockedUntil = DateTimeOffset.UtcNow + _backoff;
        _backoff = TimeSpan.FromTicks(Math.Min(_backoff.Ticks * 2, MaxBackoff.Ticks));
        return null;
    }

    public void Dispose() => _http.Dispose();
}
