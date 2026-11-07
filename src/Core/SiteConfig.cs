namespace SpankBank.Core;

/// <summary>
/// Which e621ng site this install archives: <c>SITE_HOST</c> (default e621.net), the host its files come from
/// (<c>STATIC_HOST</c>, default static1.&lt;SITE_HOST&gt;) and the short name used in texts (<c>SITE_NAME</c>, default the
/// host's first label, so "e621" or "e6ai"). Read once at start; the worker and the web app must be given the same values.
/// </summary>
public static class SiteConfig
{
    public static string Host { get; private set; } = "e621.net";
    public static string StaticHost { get; private set; } = "static1.e621.net";
    public static string Name { get; private set; } = "e621";

    static SiteConfig() => Configure(Environment.GetEnvironmentVariable);

    /// <summary>Reads the settings from the given lookup (the environment at start; tests pass their own).</summary>
    public static void Configure(Func<string, string?> get)
    {
        var host = Clean(get("SITE_HOST")) ?? "e621.net";
        Host = host;
        StaticHost = Clean(get("STATIC_HOST")) ?? "static1." + host;
        Name = Clean(get("SITE_NAME")) ?? host.Split('.')[0];
    }

    /// <summary>An address on the site, e.g. <c>Url("posts/12")</c> is https://e621.net/posts/12.</summary>
    public static string Url(string path = "") => $"https://{Host}/{path.TrimStart('/')}";

    /// <summary>An address on the file host, e.g. <c>StaticUrl("data/ab/cd/md5.jpg")</c>.</summary>
    public static string StaticUrl(string path) => $"https://{StaticHost}/{path.TrimStart('/')}";

    /// <summary>True for the site's own hosts (the site, its file host and their subdomains).</summary>
    public static bool IsSiteHost(string host)
        => Matches(host, Host) || Matches(host, StaticHost);

    private static bool Matches(string host, string own)
        => host.Equals(own, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + own, StringComparison.OrdinalIgnoreCase);

    private static string? Clean(string? value)
    {
        var text = value?.Trim().Trim('/');
        if (string.IsNullOrEmpty(text)) return null;
        // Accept a pasted address: https://e6ai.net/ -> e6ai.net
        var scheme = text.IndexOf("://", StringComparison.Ordinal);
        return scheme >= 0 ? text[(scheme + 3)..].Trim('/') : text;
    }
}
