using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SpankBank.Core;
using SpankBank.Core.Models;
using SpankBank.Data.Downloads;
using SpankBank.Data.Repositories;
using SpankBank.Web.Services;

namespace SpankBank.Web.Pages.Pools;

/// <summary>One cover picture per pool: the watched pools ("local") or a search of every pool on e621 ("online").</summary>
public class GalleryModel : PageModel
{
    public const int PageSize = 50;

    private readonly CatalogBrowser _catalog;
    private readonly WatchesRepository _watches;
    private readonly DownloadedPostSearch _downloaded;

    public GalleryModel(CatalogBrowser catalog, WatchesRepository watches, DownloadedPostSearch downloaded)
    {
        _catalog = catalog;
        _watches = watches;
        _downloaded = downloaded;
    }

    public sealed record Card(long Id, string Name, string Category, int PostCount, string? CoverUrl, string Caption);

    public string Scope { get; private set; } = Routes.Local;
    public bool IsLocal => Scope == Routes.Local;
    public IReadOnlyList<Card> Cards { get; private set; } = [];
    public long Total { get; private set; }
    public int PageNumber { get; private set; } = 1;
    public int LastPage { get; private set; } = 1;

    // online search fields
    public string? Name { get; private set; }
    public string? Description { get; private set; }
    public string? Category { get; private set; }
    public string? Status { get; private set; }
    public string Order { get; private set; } = "updated";

    public IActionResult OnGet(string scope)
    {
        if (scope is not (Routes.Local or Routes.Online)) return NotFound();
        Scope = scope;
        PageNumber = int.TryParse(Request.Query["page"], out var page) && page > 0 ? page : 1;

        if (IsLocal) LoadLocal(); else LoadOnline();
        LastPage = (int)Math.Max(1, (Total + PageSize - 1) / PageSize);
        return Page();
    }

    private void LoadLocal()
    {
        var pools = _watches.GetAll(WatchedItemType.Pool).OrderBy(w => w.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
        Total = pools.Count;
        PageNumber = Math.Min(PageNumber, Math.Max(1, (int)((Total + PageSize - 1) / PageSize)));

        var cards = new List<Card>();
        foreach (var watch in pools.Skip((PageNumber - 1) * PageSize).Take(PageSize))
        {
            var details = long.TryParse(watch.SourceValue, out var id) ? _catalog.GetPool(id) : null;
            var ids = details?.PostIds ?? [];
            var onDevice = ids.Count == 0 ? new Dictionary<long, PostListItem>() : _downloaded.GetPosts(ids).ToDictionary(p => p.Id);
            var cover = ids.Where(onDevice.ContainsKey).Select(i => onDevice[i]).FirstOrDefault();
            cards.Add(new Card(
                long.TryParse(watch.SourceValue, out var poolId) ? poolId : 0, watch.DisplayName, watch.Pool?.Category ?? "",
                ids.Count, cover is null ? null : Posts.IndexModel.PreviewUrl(cover),
                $"{onDevice.Count} of {ids.Count} local"));
        }
        Cards = cards;
    }

    private void LoadOnline()
    {
        var query = Request.Query;
        Name = query["name"].ToString();
        Description = query["description"].ToString();
        Category = query["category"].ToString() is "series" or "collection" ? query["category"].ToString() : null;
        Status = query["status"].ToString() is "active" or "inactive" ? query["status"].ToString() : null;
        Order = query["order"].ToString() is "name" or "created" or "count" ? query["order"].ToString() : "updated";

        bool? active = Status == "active" ? true : Status == "inactive" ? false : null;
        var result = _catalog.SearchPools(Name, Description, Category, active, Order, PageNumber, PageSize);
        Total = result.Total;
        var lastPage = (int)Math.Max(1, (Total + PageSize - 1) / PageSize);
        if (PageNumber > lastPage)
        {
            PageNumber = lastPage;
            result = _catalog.SearchPools(Name, Description, Category, active, Order, PageNumber, PageSize);
        }

        var covers = _catalog.CoverMd5s(result.Rows.Select(r => r.Id).ToList());
        var watched = _watches.WatchedSources(WatchedItemType.Pool);
        Cards = result.Rows.Select(r => new Card(
            r.Id, r.Name.Replace('_', ' '), r.Category, r.PostCount,
            covers.TryGetValue(r.Id, out var md5) ? SiteConfig.StaticUrl($"data/preview/{md5[..2]}/{md5[2..4]}/{md5}.jpg") : null,
            $"{r.PostCount:N0} posts{(watched.Contains(r.Id.ToString()) ? " · local" : "")}")).ToList();
    }

    public string PageUrl(int page)
        => IsLocal
            ? $"/pools/local/gallery?page={page}"
            : $"/pools/online/gallery?page={page}&name={Uri.EscapeDataString(Name ?? "")}&description={Uri.EscapeDataString(Description ?? "")}&category={Category}&status={Status}&order={Order}";
}
