using SpankBank.Core;

namespace SpankBank.Tests;

[Collection("SiteConfig")]
public class SiteConfigTests : IDisposable
{
    public void Dispose() => SiteConfig.Configure(_ => null);

    private static void Set(params (string Name, string Value)[] values)
        => SiteConfig.Configure(name => values.FirstOrDefault(v => v.Name == name).Value);

    [Fact]
    public void Without_settings_it_is_e621()
    {
        SiteConfig.Configure(_ => null);

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
        Set(("SITE_HOST", "https://example.org/"), ("STATIC_HOST", "files.example.org"), ("SITE_NAME", "Example"), ("OTHER", "x"));

        Assert.Equal(("example.org", "files.example.org", "Example"), (SiteConfig.Host, SiteConfig.StaticHost, SiteConfig.Name));
    }

    [Fact]
    public void Blank_settings_count_as_unset()
    {
        Set(("SITE_HOST", ""), ("STATIC_HOST", "  "), ("SITE_NAME", ""));

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
