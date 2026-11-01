using System.Text.RegularExpressions;

namespace SpankBank.Data.Downloads;

/// <summary>
/// What a post's source list (one URL per line) offers for getting the file back: direct file addresses on a few
/// known image hosts, and Inkbunny submission ids. Hosts outside the list are never contacted, so a source line cannot
/// point the worker at an arbitrary address.
/// </summary>
public static partial class SourceLinks
{
    /// <summary>Hosts (and their subdomains) a direct file address may be on.</summary>
    public static readonly IReadOnlyList<string> Hosts = ["inkbunny.net", "ib.metapix.net", "furaffinity.net", "facdn.net", "twimg.com"];

    private static readonly HashSet<string> FileExtensions = new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".gif", ".webp", ".webm", ".mp4" };

    /// <summary>Addresses that look like the image file itself, on an allowed host (https), in the order listed.</summary>
    public static IReadOnlyList<string> DirectFileUrls(string? source)
    {
        var urls = new List<string>();
        foreach (var line in Lines(source))
        {
            if (!Uri.TryCreate(line, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) continue;
            if (!IsAllowedHost(uri.Host) || !FileExtensions.Contains(Path.GetExtension(uri.AbsolutePath))) continue;

            var https = new UriBuilder(uri) { Scheme = Uri.UriSchemeHttps, Port = -1 }.Uri.ToString();
            if (!urls.Contains(https)) urls.Add(https);
        }
        return urls;
    }

    /// <summary>Inkbunny submission ids from <c>inkbunny.net/s/123</c>, <c>/s/123-p2</c> and <c>submissionview.php?id=123</c> links.</summary>
    public static IReadOnlyList<long> InkbunnySubmissionIds(string? source)
    {
        var ids = new List<long>();
        foreach (var line in Lines(source))
        {
            var match = InkbunnyLink().Match(line);
            if (!match.Success) continue;

            var text = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
            if (long.TryParse(text, out var id) && !ids.Contains(id)) ids.Add(id);
        }
        return ids;
    }

    /// <summary>The address without its scheme, the way the Wayback Machine's index wants it.</summary>
    public static string WithoutScheme(string url) => url.Contains("://", StringComparison.Ordinal) ? url[(url.IndexOf("://", StringComparison.Ordinal) + 3)..] : url;

    private static bool IsAllowedHost(string host)
        => Hosts.Any(allowed => host.Equals(allowed, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + allowed, StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<string> Lines(string? source)
        => (source ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    [GeneratedRegex(@"^https?://(?:www\.)?inkbunny\.net/(?:s/(\d+)|submissionview\.php\?id=(\d+))", RegexOptions.IgnoreCase)]
    private static partial Regex InkbunnyLink();
}
