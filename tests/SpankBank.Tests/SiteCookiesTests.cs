using SpankBank.Core;

namespace SpankBank.Tests;

[Collection("SiteConfig")]
public class SiteCookiesTests : IDisposable
{
    public void Dispose() => SiteConfig.Configure(SiteConfigTests.E621);

    [Fact]
    public void Each_site_gets_its_own_cookie_names()
    {
        var e621 = SiteCookies.For("e621");
        var e6ai = SiteCookies.For("e6ai");

        Assert.NotEqual(e621.Antiforgery, e6ai.Antiforgery);
        Assert.NotEqual(e621.TempData, e6ai.TempData);
        Assert.NotEqual(e621.Antiforgery, e621.TempData);
        Assert.Contains("e621", e621.Antiforgery);
        Assert.Contains("e6ai", e6ai.TempData);
    }

    [Fact]
    public void The_names_are_valid_cookie_names()
    {
        // RFC 6265 cookie-name = token: visible ASCII without separators.
        const string separators = "()<>@,;:\\\"/[]?={} \t";
        foreach (var name in new[] { SiteCookies.For("e621").Antiforgery, SiteCookies.For("e6ai").TempData })
            Assert.All(name, c => Assert.True(c > 32 && c < 127 && !separators.Contains(c), $"'{c}' in {name}"));
    }

    [Fact]
    public void Current_follows_the_configured_site()
    {
        SiteConfig.Configure(name => name == "SITE_HOST" ? "e6ai.net" : null);
        Assert.Equal(SiteCookies.For("e6ai"), SiteCookies.Current);
    }
}
