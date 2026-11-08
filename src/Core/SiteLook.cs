namespace SpankBank.Core;

/// <summary>
/// The look of the archived site: the default theme and the browser theme colour. The rest (logo, theme tweaks) is CSS,
/// picked by <c>&lt;body data-site="..."&gt;</c> (styles/sites/{e621,e6ai}.scss, base/_logos.scss).
/// Unknown site names get e621's look.
/// </summary>
public static class SiteLook
{
    /// <summary>The value for <c>data-site</c>: the site name in lower case.</summary>
    public static string Site => For(SiteConfig.Name).Site;

    public static string DefaultTheme => For(SiteConfig.Name).DefaultTheme;

    public static string ThemeColor => For(SiteConfig.Name).ThemeColor;

    public static (string Site, string DefaultTheme, string ThemeColor) For(string siteName)
    {
        var site = siteName.Trim().ToLowerInvariant();
        return site switch
        {
            "e6ai" => (site, "bloodlust", "#000000"),
            _ => (site, "hexagon", "#00549e"),
        };
    }
}
