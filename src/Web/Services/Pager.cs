namespace SpankBank.Web.Services;

/// <summary>
/// Page numbers for e621ng's paginator markup (first, last, and a window around the current page). A gap that would hide
/// only one page shows that page instead of the <c>…</c> (e621ng pull request 2836, by the SpankBank author), so
/// <c>1 … 3 [4] 5</c> reads <c>1 2 3 [4] 5</c>; gaps of two or more pages keep the <c>…</c>.
/// </summary>
public static class Pager
{
    public static IReadOnlyList<(int Page, bool Spacer)> NumberedPages(int current, int last)
    {
        var pages = new SortedSet<int> { 1, last };
        for (var p = current - 2; p <= current + 2; p++)
            if (p >= 1 && p <= last) pages.Add(p);

        var result = new List<(int, bool)>();
        var previous = 0;
        foreach (var page in pages)
        {
            if (previous != 0 && page - previous == 2)
                result.Add((previous + 1, false)); // a single hidden page is shown, not hidden behind a spacer
            else if (previous != 0 && page - previous > 2)
                result.Add((0, true));
            result.Add((page, false));
            previous = page;
        }
        return result;
    }
}
