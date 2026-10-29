using Microsoft.AspNetCore.Mvc.RazorPages;
using SpankBank.Core.Models;
using SpankBank.Data.Repositories;
using SpankBank.Web.Services;

namespace SpankBank.Web.Pages.Artists;

public class OnlineModel : PageModel
{
    public const int PageSize = 50;

    private readonly CatalogBrowser _catalog;
    private readonly WatchesRepository _watches;

    public OnlineModel(CatalogBrowser catalog, WatchesRepository watches)
    {
        _catalog = catalog;
        _watches = watches;
    }

    public string? Name { get; private set; }
    public bool ShowEmpty { get; private set; }
    /// <summary>A name filter or "show empty" is in effect: the search form starts open.</summary>
    public bool SearchActive => !string.IsNullOrWhiteSpace(Name) || ShowEmpty;
    public int PageNumber { get; private set; } = 1;
    public int LastPage { get; private set; } = 1;
    public Page<ArtistRow> Result { get; private set; } = new([], 0);
    public HashSet<string> Watched { get; private set; } = [];
    public ListView View { get; private set; } = new("/artists/online", null, null, 1, "posts", true);

    public void OnGet()
    {
        var query = Request.Query;
        Name = query["name"].ToString();
        ShowEmpty = query["empty"] == "1";
        PageNumber = int.TryParse(query["page"], out var page) && page > 0 ? page : 1;

        // Headers sort by name or post count; an old ?order= link still works.
        var sort = query["sort"].ToString();
        if (sort is not ("name" or "posts" or "id")) sort = query["order"] == "name" ? "name" : "posts";
        View = new ListView("/artists/online", sort, query["dir"], PageNumber, defaultSort: "posts", defaultDescending: true, extraQuery: ListQuery());

        Result = _catalog.SearchArtists(Name, View.Sort, hideEmpty: !ShowEmpty, PageNumber, PageSize, View.Descending);
        LastPage = (int)Math.Max(1, (Result.Total + PageSize - 1) / PageSize);
        if (PageNumber > LastPage)
        {
            PageNumber = LastPage;
            Result = _catalog.SearchArtists(Name, View.Sort, hideEmpty: !ShowEmpty, PageNumber, PageSize, View.Descending);
            View = new ListView("/artists/online", View.Sort, View.Descending ? "desc" : "asc", PageNumber, "posts", true, ListQuery());
        }
        Watched = _watches.WatchedSources(WatchedItemType.Artist);
    }

    private string ListQuery() => $"&name={Uri.EscapeDataString(Name ?? "")}" + (ShowEmpty ? "&empty=1" : "");

    public string PageUrl(int page) => View.PageUrl(page);
}
