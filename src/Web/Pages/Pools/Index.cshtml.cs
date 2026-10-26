using Microsoft.AspNetCore.Mvc.RazorPages;
using SpankBank.Core.Models;
using SpankBank.Data.Repositories;

namespace SpankBank.Web.Pages.Pools;

public class IndexModel : PageModel
{
    public const int PageSize = 50;

    private readonly CatalogBrowser _catalog;
    private readonly WatchesRepository _watches;

    public IndexModel(CatalogBrowser catalog, WatchesRepository watches)
    {
        _catalog = catalog;
        _watches = watches;
    }

    public string? Name { get; private set; }
    public string? Description { get; private set; }
    public string? Category { get; private set; }
    public string? Status { get; private set; }
    public string Order { get; private set; } = "updated";
    public int PageNumber { get; private set; } = 1;
    public int LastPage { get; private set; } = 1;
    public Page<PoolRow> Result { get; private set; } = new([], 0);
    public HashSet<string> Watched { get; private set; } = [];

    public void OnGet()
    {
        var query = Request.Query;
        Name = query["name"].ToString();
        Description = query["description"].ToString();
        Category = query["category"].ToString() is "series" or "collection" ? query["category"].ToString() : null;
        Status = query["status"].ToString() is "active" or "inactive" ? query["status"].ToString() : null;
        Order = query["order"].ToString() is "name" or "created" or "count" ? query["order"].ToString() : "updated";
        PageNumber = int.TryParse(query["page"], out var page) && page > 0 ? page : 1;

        bool? active = Status == "active" ? true : Status == "inactive" ? false : null;
        Result = _catalog.SearchPools(Name, Description, Category, active, Order, PageNumber, PageSize);
        LastPage = (int)Math.Max(1, (Result.Total + PageSize - 1) / PageSize);
        if (PageNumber > LastPage)
        {
            PageNumber = LastPage;
            Result = _catalog.SearchPools(Name, Description, Category, active, Order, PageNumber, PageSize);
        }
        Watched = _watches.WatchedSources(WatchedItemType.Pool);
    }

    public string PageUrl(int page)
        => $"/pools/online?page={page}&name={Uri.EscapeDataString(Name ?? "")}&description={Uri.EscapeDataString(Description ?? "")}"
         + $"&category={Category}&status={Status}&order={Order}";
}
