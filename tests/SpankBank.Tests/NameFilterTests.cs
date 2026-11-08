using SpankBank.Core;

namespace SpankBank.Tests;

public class NameFilterTests
{
    [Theory]
    [InlineData("", "anything", true)]
    [InlineData(null, "anything", true)]
    [InlineData("fox", "Red_Fox_Tails", true)]       // plain text matches anywhere, case and _/space ignored
    [InlineData("red fox", "red_fox_tails", true)]
    [InlineData("wolf", "red_fox_tails", false)]
    [InlineData("red*", "red_fox_tails", true)]       // with a wildcard the whole name must match
    [InlineData("fox*", "red_fox_tails", false)]
    [InlineData("*tails", "red_fox_tails", true)]
    [InlineData("r*x*s", "red_fox_tails", true)]
    [InlineData("a.c", "abc", false)]                 // regex characters are literal
    public void Matches(string? pattern, string value, bool expected) => Assert.Equal(expected, NameFilter.Matches(pattern, value));

    [Theory]
    [InlineData("fox", "%fox%")]
    [InlineData("Red_Fox", "%red fox%")]
    [InlineData("fox*", "fox%")]
    [InlineData("50%", "%50\\%%")]
    public void ToLike_escapes_and_turns_the_wildcard_into_percent(string pattern, string expected) => Assert.Equal(expected, NameFilter.ToLike(pattern));
}
