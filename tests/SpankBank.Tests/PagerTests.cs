using SpankBank.Web.Services;

namespace SpankBank.Tests;

public class PagerTests
{
    private static string Render(int current, int last)
        => string.Join(' ', Pager.NumberedPages(current, last).Select(p => p.Spacer ? "…" : p.Page.ToString()));

    [Theory]
    [InlineData(4, 9, "1 2 3 4 5 6 … 9")]        // page 2 is shown, not hidden behind a spacer
    [InlineData(6, 9, "1 … 4 5 6 7 8 9")]        // the page before the last is shown too
    [InlineData(5, 8, "1 2 3 4 5 6 7 8")]        // single-page gaps on both sides are filled
    [InlineData(8, 8, "1 … 6 7 8")]
    [InlineData(10, 20, "1 … 8 9 10 11 12 … 20")] // gaps of two or more pages keep the spacer
    [InlineData(5, 20, "1 2 3 4 5 6 7 … 20")]
    [InlineData(1, 1, "1")]
    public void A_spacer_never_hides_just_one_page(int current, int last, string expected)
        => Assert.Equal(expected, Render(current, last));
}
