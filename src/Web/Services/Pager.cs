namespace SpankBank.Web.Services;

/// <summary>Page numbers for e621ng's paginator markup (first, last, and a window around the current page).</summary>
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
            if (previous != 0 && page - previous > 1)
                result.Add((0, true));
            result.Add((page, false));
            previous = page;
        }
        return result;
    }
}
