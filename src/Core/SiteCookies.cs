namespace SpankBank.Core;

/// <summary>
/// Names of the web app's own cookies, one set per site. Browsers keep cookies per host, not per port, so two instances
/// reached on the same address (e621 on :8621, e6ai on :8681) would otherwise overwrite each other's antiforgery and
/// TempData cookies: each instance has its own key ring, so a form posted after visiting the other site was rejected.
/// </summary>
public static class SiteCookies
{
    /// <summary>The cookie names for the site with this short name (<see cref="SiteConfig.Name"/>, e.g. "e621").</summary>
    public static (string Antiforgery, string TempData) For(string siteName)
        => ($".SpankBank.{siteName}.Antiforgery", $".SpankBank.{siteName}.TempData");

    /// <summary>The cookie names of the configured site.</summary>
    public static (string Antiforgery, string TempData) Current => For(SiteConfig.Name);
}
