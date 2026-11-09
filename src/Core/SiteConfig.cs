namespace SpankBank.Core;

/// <summary>
/// Which e621ng site this install archives: <c>SITE_HOST</c> (required, no default: an instance whose setting went
/// missing must not quietly archive e621), the host its files come from (<c>STATIC_HOST</c>, default
/// static1.&lt;SITE_HOST&gt;) and the short name used in texts, the host's first label ("e621", "e6ai"). Read once at
/// start by <see cref="ConfigureFromEnvironment"/>; the worker and the web app of one instance need the same values.
/// Until configured (tests only) the values are e621's.
/// </summary>
public static class SiteConfig
{
    public static string Host { get; private set; } = "e621.net";
    public static string StaticHost { get; private set; } = "static1.e621.net";
    public static string Name { get; private set; } = "e621";

    /// <summary>Reads the settings from the environment; throws when <c>SITE_HOST</c> is not set. Call it first thing at start.</summary>
    public static void ConfigureFromEnvironment() => Configure(Environment.GetEnvironmentVariable);

    /// <summary>Reads the settings from the given lookup (tests pass their own).</summary>
    public static void Configure(Func<string, string?> get)
    {
        var host = Clean(get("SITE_HOST"))
            ?? throw new InvalidOperationException(
                "SITE_HOST is not set. Set it to the e621ng site this instance archives (for example e621.net or e6ai.net); there is no default.");
        Host = host;
        StaticHost = Clean(get("STATIC_HOST")) ?? "static1." + host;
        Name = host.Split('.')[0];
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
