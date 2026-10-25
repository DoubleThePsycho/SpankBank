using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SpankBank.Core.Models;
using SpankBank.Data.Repositories;

namespace SpankBank.Web.Pages.Watches;

public partial class IndexModel : PageModel
{
    private readonly WatchesRepository _watches;

    public IndexModel(WatchesRepository watches) => _watches = watches;

    [TempData] public string? Message { get; set; }
    [TempData] public string? Error { get; set; }

    public IReadOnlyList<WatchEntry> Artists { get; private set; } = [];
    public IReadOnlyList<WatchEntry> Pools { get; private set; } = [];
    public IReadOnlyList<WatchEntry> Searches { get; private set; } = [];

    public void OnGet()
    {
        Artists = _watches.GetAll(WatchedItemType.Artist);
        Pools = _watches.GetAll(WatchedItemType.Pool);
        Searches = _watches.GetAll(WatchedItemType.Search);
    }

    public JsonResult OnGetSuggest(string? term) => new(_watches.SuggestArtists(term ?? ""));

    public IActionResult OnPostAddArtist(string? artist)
    {
        if (string.IsNullOrWhiteSpace(artist))
            return Fail("Enter an artist tag.");

        var tag = _watches.ResolveArtistTag(artist);
        if (!_watches.IsArtistTag(tag))
            return Fail($"\"{tag}\" is not an artist tag in the catalog.");

        var id = _watches.Add(WatchedItemType.Artist, tag, tag);
        if (id is null)
            return Fail($"{tag} is already watched.");

        _watches.RefreshArtist(id.Value, tag);
        return Done($"Added artist {tag}.");
    }

    public IActionResult OnPostAddPool(string? pool)
    {
        var match = pool is null ? null : PoolReference().Match(pool.Trim());
        if (match is null || !match.Success)
            return Fail("Enter a pool ID or a pool URL.");

        var id = match.Groups["id"].Value;
        return _watches.Add(WatchedItemType.Pool, id, $"Pool #{id}") is null
            ? Fail($"Pool #{id} is already watched.")
            : Done($"Added pool #{id}.");
    }

    public IActionResult OnPostAddSearch(string? query)
    {
        var text = string.Join(' ', (query ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (text.Length == 0)
            return Fail("Enter a search.");

        return _watches.Add(WatchedItemType.Search, text, text) is null
            ? Fail("That search is already watched.")
            : Done("Added search.");
    }

    public IActionResult OnPostToggle(long id, bool enabled)
    {
        _watches.SetEnabled(id, enabled);
        return RedirectToPage();
    }

    public IActionResult OnPostDelete(long id)
    {
        _watches.Delete(id);
        return RedirectToPage();
    }

    public IActionResult OnPostToggleTag(long tagId, bool enabled)
    {
        _watches.SetArtistTagEnabled(tagId, enabled);
        return RedirectToPage();
    }

    public IActionResult OnPostRemoveTag(long tagId)
    {
        _watches.DeleteArtistTag(tagId);
        return RedirectToPage();
    }

    public IActionResult OnPostAddTag(long id, string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
            return Fail("Enter a tag.");

        _watches.AddArtistTag(id, WatchesRepository.Normalize(tag), ArtistTagKind.Character, isManual: true);
        return RedirectToPage();
    }

    public IActionResult OnPostRescan(long id, string artist)
    {
        _watches.RefreshArtist(id, artist);
        return Done($"Rescanned {artist}.");
    }

    private IActionResult Fail(string message)
    {
        Error = message;
        return RedirectToPage();
    }

    private IActionResult Done(string message)
    {
        Message = message;
        return RedirectToPage();
    }

    [GeneratedRegex(@"^(?:.*/pools/)?(?<id>\d+)(?:[/?#].*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex PoolReference();
}
