namespace SpankBank.Data.Downloads;

// Getting a deleted post's file back from the places its source list names, before asking the Wayback Machine.
public sealed partial class PostFetcher
{
    private static readonly TimeSpan InkbunnyPause = TimeSpan.FromHours(1);

    private readonly RateGate _sources = new(TimeSpan.FromSeconds(2));
    private readonly HashSet<long> _sourcesTried = [];
    private InkbunnyClient? _inkbunny;
    private DateTimeOffset _inkbunnyOffUntil = DateTimeOffset.MinValue;

    /// <summary>Told about things worth a log line (a refused Inkbunny login, running as guest).</summary>
    public Action<string>? Notice { get; set; }

    /// <summary>The Inkbunny account, normally from INKBUNNY_USERNAME / INKBUNNY_PASSWORD; blank means the guest login.</summary>
    public (string? Username, string? Password) InkbunnyAccount { get; set; } =
        (Environment.GetEnvironmentVariable("INKBUNNY_USERNAME"), Environment.GetEnvironmentVariable("INKBUNNY_PASSWORD"));

    /// <returns>The outcome when a source had the file (md5 verified); null when none did. A post is tried once per run.</returns>
    private async Task<FetchOutcome?> RecoverFromSourcesAsync(CatalogPost post, string original, CancellationToken cancellationToken, Action<long, long?>? onProgress)
    {
        if (string.IsNullOrWhiteSpace(post.Source)) return null;
        lock (_sourcesTried) if (!_sourcesTried.Add(post.Id)) return null;

        foreach (var url in SourceLinks.DirectFileUrls(post.Source).Take(4))
        {
            var result = await DownloadAsync(url, original, post.Md5, jpegOnly: false, StallTimeout, cancellationToken, _sources, onProgress);
            if (result.Kind == DownloadKind.Ok)
                return new FetchOutcome(FetchResult.Recovered, $"Original recovered from its source ({new Uri(url).Host}).");
        }

        foreach (var id in SourceLinks.InkbunnySubmissionIds(post.Source).Take(3))
        {
            if (DateTimeOffset.UtcNow < _inkbunnyOffUntil) break;

            var client = Inkbunny();
            var files = await client.FilesAsync(id, cancellationToken);
            if (files is null)
            {
                _inkbunnyOffUntil = DateTimeOffset.UtcNow + InkbunnyPause;
                lock (_sourcesTried) _sourcesTried.Remove(post.Id); // ask again once Inkbunny answers
                Notice?.Invoke($"Inkbunny did not answer ({client.Problem}); leaving it alone for an hour.");
                break;
            }

            foreach (var file in files)
            {
                // The md5 says it is not this file; without one, only a file of the same type is worth fetching.
                if (file.Md5 is { Length: 32 } md5 ? !md5.Equals(post.Md5, StringComparison.OrdinalIgnoreCase)
                                                    : !file.Url.EndsWith("." + post.Ext, StringComparison.OrdinalIgnoreCase))
                    continue;

                var result = await DownloadAsync(file.Url, original, post.Md5, jpegOnly: false, StallTimeout, cancellationToken, _sources, onProgress);
                if (result.Kind == DownloadKind.Ok)
                    return new FetchOutcome(FetchResult.Recovered, $"Original recovered from Inkbunny (submission {id}).");
            }
        }

        return null;
    }

    private InkbunnyClient Inkbunny()
    {
        if (_inkbunny is not null) return _inkbunny;

        _inkbunny = new InkbunnyClient(_http, _sources.WaitAsync, InkbunnyAccount.Username, InkbunnyAccount.Password);
        if (_inkbunny.IsGuest)
            Notice?.Invoke("No Inkbunny account is set (INKBUNNY_USERNAME / INKBUNNY_PASSWORD): using the guest login, which only shows general-rated art.");
        return _inkbunny;
    }
}
