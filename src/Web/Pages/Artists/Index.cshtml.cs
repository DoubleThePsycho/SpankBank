using Microsoft.AspNetCore.Mvc.RazorPages;
using SpankBank.Core.Models;
using SpankBank.Data.Repositories;

namespace SpankBank.Web.Pages.Artists;

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
    public string Order { get; private set; } = "count";
    public bool ShowEmpty { get; private set; }
    public int PageNumber { get; private set; } = 1;
    public int LastPage { get; private set; } = 1;
    public Page<ArtistRow> Result { get; private set; } = new([], 0);
    public HashSet<string> Watched { get; private set; } = [];

    public void OnGet()
    {
        var query = Request.Query;
        Name = query["name"].ToString();
        Order = query["order"] == "name" ? "name" : "count";
        ShowEmpty = query["empty"] == "1";
        PageNumber = int.TryParse(query["page"], out var page) && page > 0 ? page : 1;

        Result = _catalog.SearchArtists(Name, Order, hideEmpty: !ShowEmpty, PageNumber, PageSize);
        LastPage = (int)Math.Max(1, (Result.Total + PageSize - 1) / PageSize);
        if (PageNumber > LastPage)
        {
            PageNumber = LastPage;
            Result = _catalog.SearchArtists(Name, Order, hideEmpty: !ShowEmpty, PageNumber, PageSize);
        }
        Watched = _watches.WatchedSources(WatchedItemType.Artist);
    }

    public string PageUrl(int page)
        => $"/artists?page={page}&name={Uri.EscapeDataString(Name ?? "")}&order={Order}" + (ShowEmpty ? "&empty=1" : "");
}
