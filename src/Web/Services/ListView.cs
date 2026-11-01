namespace SpankBank.Web.Services;

/// <summary>Sorting and paging state of a list page: builds the header and pager links.</summary>
public sealed class ListView
{
    // Count columns are most useful biggest first, so their first click sorts descending.
    private static readonly HashSet<string> DescendingFirst = ["total", "downloaded", "restored", "lost", "pending", "pages", "local", "found", "enabled", "custom", "aliases", "posts"];

    private readonly string _path;
    private readonly string _extraQuery;

    public ListView(string path, string? sort, string? dir, int page, string defaultSort, bool defaultDescending, string extraQuery = "")
    {
        _path = path;
        _extraQuery = extraQuery;
        Sort = string.IsNullOrWhiteSpace(sort) ? defaultSort : sort;
        Descending = dir is "desc" ? true : dir is "asc" ? false : (Sort == defaultSort ? defaultDescending : DescendingFirst.Contains(Sort));
        Page = Math.Max(1, page);
    }

    public string Sort { get; }
    public bool Descending { get; }
    public int Page { get; }

    /// <summary>The current page with its sorting, for forms that must come back to the same place.</summary>
    public string Current => Url(Sort, Descending, Page);

    public string SortUrl(string column)
        => Url(column, Sort == column ? !Descending : DescendingFirst.Contains(column), 1);

    public string PageUrl(int page) => Url(Sort, Descending, page);

    public string AriaSort(string column) => Sort != column ? "none" : Descending ? "descending" : "ascending";

    /// <summary>The caret for the active column, else empty. Shown inside a fixed-width span so it never changes the column width.</summary>
    public string Mark(string column) => Sort != column ? "" : Descending ? "\u25BC" : "\u25B2";

    private string Url(string sort, bool descending, int page)
        => $"{_path}?sort={sort}&dir={(descending ? "desc" : "asc")}&page={page}{_extraQuery}";

    public static int PageSize(string? configured)
        => int.TryParse(configured, out var size) ? Math.Clamp(size, 10, 1000) : Data.Repositories.WatchesRepository.DefaultListPageSize;
}
