using SpankBank.Core;

namespace SpankBank.Tests;

[Collection("SiteConfig")]
public class SiteConfigTests : IDisposable
{
    public void Dispose() => SiteConfig.Configure(E621);

    /// <summary>The lookup of an e621 install; the tests' starting point.</summary>
    public static string? E621(string name) => name == "SITE_HOST" ? "e621.net" : null;

    private static void Set(params (string Name, string Value)[] values)
        => SiteConfig.Configure(name => values.FirstOrDefault(v => v.Name == name).Value);

    [Fact]
    public void Without_a_host_it_refuses_instead_of_defaulting_to_e621()
    {
        var error = Assert.Throws<InvalidOperationException>(() => SiteConfig.Configure(_ => null));
        Assert.Contains("SITE_HOST", error.Message);
        Assert.Throws<InvalidOperationException>(() => SiteConfig.Configure(name => name == "STATIC_HOST" ? "files.example.org" : "  "));
    }

    [Fact]
    public void Only_the_host_is_needed_for_e621()
    {
        SiteConfig.Configure(E621);

        Assert.Equal(("e621.net", "static1.e621.net", "e621"), (SiteConfig.Host, SiteConfig.StaticHost, SiteConfig.Name));
        Assert.Equal("https://e621.net/posts/5", SiteConfig.Url("posts/5"));
        Assert.Equal("https://static1.e621.net/data/ab/cd/x.jpg", SiteConfig.StaticUrl("/data/ab/cd/x.jpg"));
    }

    [Fact]
    public void The_host_gives_the_file_host_and_the_name()
    {
        Set(("SITE_HOST", "e6ai.net"));

        Assert.Equal(("e6ai.net", "static1.e6ai.net", "e6ai"), (SiteConfig.Host, SiteConfig.StaticHost, SiteConfig.Name));
    }

    [Fact]
    public void Each_value_can_be_set_and_a_pasted_address_is_cleaned()
    {
        Set(("SITE_HOST", "https://example.org/"), ("STATIC_HOST", "files.example.org"), ("SITE_NAME", "Ignored"), ("OTHER", "x"));

        // SITE_NAME is no longer read: the name comes from the host.
        Assert.Equal(("example.org", "files.example.org", "example"), (SiteConfig.Host, SiteConfig.StaticHost, SiteConfig.Name));
    }

    [Fact]
    public void A_blank_file_host_counts_as_unset()
    {
        Set(("SITE_HOST", "e621.net"), ("STATIC_HOST", "  "));

        Assert.Equal(("e621.net", "static1.e621.net", "e621"), (SiteConfig.Host, SiteConfig.StaticHost, SiteConfig.Name));
    }

    [Theory]
    [InlineData("e6ai.net", true)]
    [InlineData("static1.e6ai.net", true)]
    [InlineData("WWW.E6AI.NET", true)]
    [InlineData("e621.net", false)]
    [InlineData("static1.e621.net", false)]
    [InlineData("note6ai.net", false)]
    [InlineData("e6ai.net.evil.example", false)]
    public void Only_the_sites_own_hosts_count(string host, bool own)
    {
        Set(("SITE_HOST", "e6ai.net"));

        Assert.Equal(own, SiteConfig.IsSiteHost(host));
    }
}
