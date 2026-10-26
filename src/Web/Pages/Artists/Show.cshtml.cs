using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SpankBank.Core.Models;
using SpankBank.Data.Repositories;

namespace SpankBank.Web.Pages.Artists;

public class ShowModel : PageModel
{
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

    public ArtistRow Artist { get; private set; } = null!;
    public WatchEntry? Watch { get; private set; }
    public IReadOnlyList<string> Characters { get; private set; } = [];
    public IReadOnlyList<PostListItem> RecentPosts { get; private set; } = [];
    public long DownloadedCount { get; private set; }

    public IActionResult OnGet(string name)
    {
        var artist = _catalog.GetArtist(name.ToLowerInvariant().Replace(' ', '_'));
        if (artist is null) return NotFound();
        if (!string.Equals(artist.Name, name, StringComparison.Ordinal))
            return RedirectToPage(null, new { name = artist.Name }); // an alias: go to the real name

        Artist = artist;
        Watch = _watches.GetAll(WatchedItemType.Artist).FirstOrDefault(w => string.Equals(w.SourceValue, artist.Name, StringComparison.OrdinalIgnoreCase));
        Characters = _watches.FindCharacters([artist.Name, .. artist.OtherNames]);

        var local = _downloaded.Search(artist.Name, 1, 24);
        RecentPosts = local.Items;
        DownloadedCount = local.Total;
        return Page();
    }

    public IActionResult OnPostWatch(string name)
    {
        var tag = _watches.ResolveArtistTag(name);
        if (!_watches.IsArtistTag(tag)) return NotFound();

        var id = _watches.Add(WatchedItemType.Artist, tag, tag);
        if (id is not null) _watches.RefreshArtist(id.Value, tag);
        Message = id is null ? $"{tag} is already watched." : $"Now watching {tag}. Matching starts right away; downloads follow.";
        return RedirectToPage(null, new { name = tag });
    }
}
