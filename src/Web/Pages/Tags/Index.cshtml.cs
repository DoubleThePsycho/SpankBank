using Microsoft.AspNetCore.Mvc.RazorPages;
using SpankBank.Data.Repositories;

namespace SpankBank.Web.Pages.Tags;

public class IndexModel : PageModel
{
    public const int PageSize = 75;

    private readonly CatalogBrowser _catalog;
    private readonly LocalTagCounts _localCounts;
    private IReadOnlyDictionary<string, int> _local = new Dictionary<string, int>();

    public IndexModel(CatalogBrowser catalog, LocalTagCounts localCounts)
    {
        _catalog = catalog;
        _localCounts = localCounts;
    }

    /// <summary>How many posts on this device carry the tag.</summary>
    public int LocalCount(string tag) => _local.GetValueOrDefault(tag);

    // e621's categories in the order its search form lists them.
    public static readonly (int Id, string Name)[] Categories =
        [(0, "General"), (1, "Artist"), (3, "Copyright"), (4, "Character"), (5, "Species"), (6, "Invalid"), (7, "Meta"), (8, "Lore")];

    public string? Name { get; private set; }
    public int? Category { get; private set; }
    public string Order { get; private set; } = "count";
    public bool ShowEmpty { get; private set; }

    /// <summary>The folded search form is open while a search is active.</summary>
    public bool SearchActive => !string.IsNullOrWhiteSpace(Name) || Category is not null || Order != "count" || ShowEmpty;
    public int PageNumber { get; private set; } = 1;
    public int LastPage { get; private set; } = 1;
    public Page<TagRow> Result { get; private set; } = new([], 0);

    public void OnGet()
    {
        var query = Request.Query;
        Name = query["name"].ToString();
        Category = int.TryParse(query["category"], out var category) ? category : null;
        Order = query["order"].ToString() is "name" or "date" ? query["order"].ToString() : "count";
        ShowEmpty = query["empty"] == "1";
        PageNumber = int.TryParse(query["page"], out var page) && page > 0 ? page : 1;

        Result = _catalog.SearchTags(Name, Category, Order, hideEmpty: !ShowEmpty, PageNumber, PageSize);
        LastPage = (int)Math.Max(1, (Result.Total + PageSize - 1) / PageSize);
        if (PageNumber > LastPage)
        {
            PageNumber = LastPage;
            Result = _catalog.SearchTags(Name, Category, Order, hideEmpty: !ShowEmpty, PageNumber, PageSize);
        }
        _local = _localCounts.Get(Result.Rows.Select(r => r.Name));
    }

    public string PageUrl(int page)
        => $"/tags?page={page}&name={Uri.EscapeDataString(Name ?? "")}&category={Category}&order={Order}" + (ShowEmpty ? "&empty=1" : "");
}
