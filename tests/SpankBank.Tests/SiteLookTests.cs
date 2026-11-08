using SpankBank.Core;
using Xunit;

namespace SpankBank.Tests;

public class SiteLookTests
{
    [Fact]
    public void E621_gets_the_stock_hexagon_look()
        => Assert.Equal(("e621", "hexagon", "#00549e"), SiteLook.For("e621"));

    [Theory]
    [InlineData("e6ai")]
    [InlineData("E6AI")]
    public void E6ai_gets_bloodlust_and_a_black_theme_colour(string name)
        => Assert.Equal(("e6ai", "bloodlust", "#000000"), SiteLook.For(name));

    [Fact]
    public void An_unknown_site_falls_back_to_the_stock_look_but_keeps_its_name()
        => Assert.Equal(("example", "hexagon", "#00549e"), SiteLook.For("example"));
}
