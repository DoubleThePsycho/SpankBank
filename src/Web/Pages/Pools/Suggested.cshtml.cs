using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SpankBank.Data.Repositories;

namespace SpankBank.Web.Pages.Pools;

/// <summary>Pools that hold posts of watched artists and are waiting for the user to add or ignore them.</summary>
public class SuggestedModel : PageModel
{
    private readonly WatchesRepository _watches;

    public SuggestedModel(WatchesRepository watches) => _watches = watches;

    [TempData] public string? Message { get; set; }

    public IReadOnlyList<PoolSuggestion> Suggestions { get; private set; } = [];
    public bool ShowAll { get; private set; }

    public void OnGet()
    {
        ShowAll = Request.Query["all"] == "1";
        Suggestions = _watches.GetPoolSuggestions(ShowAll);
    }

    public IActionResult OnPostAcceptSuggestion(long poolId, string? artist)
    {
        _watches.AcceptPoolSuggestion(poolId, artist ?? "");
        Message = $"Added pool #{poolId}.";
        return RedirectToPage(null, null, new { all = Request.Query["all"] == "1" ? "1" : null });
    }

    public IActionResult OnPostIgnoreSuggestion(long artistId, long poolId)
    {
        _watches.IgnorePoolSuggestion(artistId, poolId);
        return RedirectToPage(null, null, new { all = Request.Query["all"] == "1" ? "1" : null });
    }
}
