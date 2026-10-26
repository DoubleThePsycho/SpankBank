using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SpankBank.Core.Models;
using SpankBank.Data.Repositories;

namespace SpankBank.Web.Pages.Pools;

public class ShowModel : PageModel
{
    public const int PageSize = 75;

    private readonly CatalogBrowser _catalog;
    private readonly WatchesRepository _watches;
    private readonly DownloadedPostSearch _downloaded;

    public ShowModel(CatalogBrowser catalog, WatchesRepository watches, DownloadedPostSearch downloaded)
    {
        _catalog = catalog;
        _watches = watches;
        _downloaded = downloaded;
    }

    [TempData] public string? Message { get; set; }

    public PoolDetails Pool { get; private set; } = null!;
    public WatchEntry? Watch { get; private set; }
    public IReadOnlyList<PostListItem> Posts { get; private set; } = [];
    public int OnDevice { get; private set; }
    public int PageNumber { get; private set; } = 1;
    public int LastPage { get; private set; } = 1;

    public IActionResult OnGet(long id)
    {
        var pool = _catalog.GetPool(id);
        if (pool is null) return NotFound();

        Pool = pool;
        Watch = _watches.GetAll(WatchedItemType.Pool).FirstOrDefault(w => w.SourceValue == id.ToString());

        // The pool's order, restricted to what is on this device.
        var onDevice = _downloaded.GetPosts(pool.PostIds).ToDictionary(p => p.Id);
        var ordered = pool.PostIds.Where(onDevice.ContainsKey).Select(postId => onDevice[postId]).ToList();
        OnDevice = ordered.Count;

        LastPage = Math.Max(1, (ordered.Count + PageSize - 1) / PageSize);
        PageNumber = Math.Min(LastPage, int.TryParse(Request.Query["page"], out var page) && page > 0 ? page : 1);
        Posts = ordered.Skip((PageNumber - 1) * PageSize).Take(PageSize).ToList();
        return Page();
    }

    public IActionResult OnPostWatch(long id)
    {
        var pool = _catalog.GetPool(id);
        if (pool is null) return NotFound();

        var added = _watches.Add(WatchedItemType.Pool, id.ToString(), $"Pool #{id}", origin: "manual");
        Message = added is null ? "This pool is already watched." : "Now watching this pool. Matching starts right away; downloads follow.";
        return RedirectToPage(null, new { id });
    }

    public string PageUrl(int page) => $"/pools/{Pool.Id}?page={page}";
}
