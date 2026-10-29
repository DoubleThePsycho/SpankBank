using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SpankBank.Core.Models;
using SpankBank.Data.Repositories;
using SpankBank.Web.Services;

namespace SpankBank.Web.Pages.Artists;

public class ShowModel : PageModel
{
    private readonly CatalogBrowser _catalog;
    private readonly WatchesRepository _watches;
    private readonly DownloadedPostSearch _downloaded;
    private readonly ArtistPostRemoval _removal;
    private readonly WorkerClient _worker;

    public ShowModel(CatalogBrowser catalog, WatchesRepository watches, DownloadedPostSearch downloaded, ArtistPostRemoval removal, WorkerClient worker)
    {
        _catalog = catalog;
        _watches = watches;
        _downloaded = downloaded;
        _removal = removal;
        _worker = worker;
    }

    [TempData] public string? Message { get; set; }
    [TempData] public string? Error { get; set; }

    private bool IsFetch => Request.Headers["X-Fetch"] == "1";

    public ArtistRow Artist { get; private set; } = null!;
    public WatchEntry? Watch { get; private set; }
    public IReadOnlyList<string> Characters { get; private set; } = [];
    public IReadOnlyList<PostListItem> RecentPosts { get; private set; } = [];
    public long DownloadedCount { get; private set; }
    /// <summary>Pools that were added for this artist (automatically or from a suggestion).</summary>
    public IReadOnlyList<WatchEntry> WatchedPools { get; private set; } = [];
    public IReadOnlyList<PoolSuggestion> Suggestions { get; private set; } = [];
    public bool ShowAllSuggestions { get; private set; }

    public IActionResult OnGet(string name)
    {
        var artist = _catalog.GetArtist(name.ToLowerInvariant().Replace(' ', '_'));
        if (artist is null) return NotFound();
        if (!string.Equals(artist.Name, name, StringComparison.Ordinal))
            return RedirectToPage(null, new { name = artist.Name }); // an alias: go to the real name

        Artist = artist;
        Watch = _watches.GetAll(WatchedItemType.Artist).FirstOrDefault(w => string.Equals(w.SourceValue, artist.Name, StringComparison.OrdinalIgnoreCase));
        Characters = _watches.FindCharacters([artist.Name, .. artist.OtherNames]);

        if (Watch is not null)
        {
            ShowAllSuggestions = Request.Query["all"] == "1";
            Suggestions = _watches.GetPoolSuggestions(ShowAllSuggestions, Watch.Id);
            WatchedPools = _watches.GetAll(WatchedItemType.Pool)
                .Where(p => p.Origin is { } origin && (origin == $"auto:{artist.Name}" || origin == $"suggested:{artist.Name}"))
                .ToList();
        }

        var local = _downloaded.Search(artist.Name, 1, 24);
        RecentPosts = local.Items;
        DownloadedCount = local.Total;
        return Page();
    }

    public IActionResult OnPostAcceptSuggestion(long poolId, string name)
    {
        _watches.AcceptPoolSuggestion(poolId, name);
        return RedirectToPage(null, null, new { name }, "pools");
    }

    public IActionResult OnPostIgnoreSuggestion(long artistId, long poolId, string name)
    {
        _watches.IgnorePoolSuggestion(artistId, poolId);
        return RedirectToPage(null, null, new { name }, "pools");
    }

    public IActionResult OnPostWatch(string name)
    {
        var tag = _watches.ResolveArtistTag(name);
        if (!_watches.IsArtistTag(tag)) return NotFound();

        // Added disabled: nothing is matched or downloaded until the artist is enabled below.
        var id = _watches.Add(WatchedItemType.Artist, tag, tag, enabled: false);
        if (id is not null) _watches.RefreshArtist(id.Value, tag);
        Message = id is null ? $"{tag} is already watched." : $"Added {tag}, not enabled yet. Adjust the characters and filters, then press Enable.";
        return Back(tag);
    }

    // ---------------------------------------------------------------- the artist's watch settings

    private IActionResult Back(string name) => RedirectToPage(null, null, new { name }, "watch");

    public IActionResult OnPostToggle(string name, long id, bool enabled)
    {
        _watches.SetEnabled(id, enabled);
        Message = enabled ? "Enabled. Matching starts now; downloads follow." : "Disabled.";
        return Back(name);
    }

    public IActionResult OnPostRescan(string name, long id)
    {
        _watches.RefreshArtist(id, name);
        Message = "Looked for new aliases and characters.";
        return Back(name);
    }

    public async Task<IActionResult> OnPostIgnoreBlacklistAsync(string name, long id, bool ignore)
    {
        _watches.SetIgnoreBlacklist(id, ignore);
        await _worker.NotifyAsync("downloads");
        return Back(name);
    }

    /// <summary>Turns one character on or off. Answers 204 to the page's script, a redirect to a plain form post.</summary>
    public IActionResult OnPostToggleTag(string name, long tagId, bool enabled)
    {
        _watches.SetArtistTagEnabled(tagId, enabled);
        return IsFetch ? new NoContentResult() : Back(name);
    }

    public IActionResult OnPostRemoveTag(string name, long tagId)
    {
        _watches.DeleteArtistTag(tagId);
        return IsFetch ? new NoContentResult() : Back(name);
    }

    public IActionResult OnPostSetAllTags(string name, long id, bool enabled)
    {
        _watches.SetAllArtistTags(id, enabled);
        return Back(name);
    }

    public IActionResult OnPostAddTag(string name, long id, string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
            Error = "Enter a character tag.";
        else
        {
            var character = WatchesRepository.Normalize(tag);
            if (!_watches.IsCharacterTag(character)) Error = $"\"{character}\" is not a character tag in the catalog.";
            else _watches.AddArtistTag(id, character, ArtistTagKind.Character, isManual: true);
        }
        return Back(name);
    }

    public IActionResult OnPostSaveFilters(string name, long id, string? whitelist, string? blacklist)
    {
        if (_watches.SetFilters(id, whitelist, blacklist) is { } problem) Error = problem;
        else Message = "Filters saved. The artist's posts are matched again with them.";
        return Back(name);
    }

    /// <summary>What removing this artist can do to their downloaded posts (the page asks before it removes anyone).</summary>
    public IActionResult OnGetRemovalCounts(string name, long id) => new JsonResult(_removal.Counts(id));

    public async Task<IActionResult> OnPostDeleteAsync(string name, long id, string? posts)
    {
        if (posts is "all" or "exclusive" && _removal.Remove(id, exclusiveOnly: posts == "exclusive") > 0)
            await _worker.NotifyAsync("bin");
        _watches.Delete(id);
        TempData["Message"] = $"Removed {name}.";
        return Redirect("/artists/local");
    }
}
