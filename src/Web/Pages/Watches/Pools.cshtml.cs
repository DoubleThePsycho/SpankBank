using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using SpankBank.Core.Models;
using SpankBank.Data.Repositories;

namespace SpankBank.Web.Pages.Watches;

public partial class PoolsModel : WatchPageModel
{
    public PoolsModel(WatchesRepository watches) : base(watches) { }

    protected override WatchedItemType ItemType => WatchedItemType.Pool;
    protected override string ModalBodyPartial => "_PoolModalBody";

    public IReadOnlyList<WatchEntry> Entries { get; private set; } = [];
    public IReadOnlyList<PoolSuggestion> Suggestions { get; private set; } = [];
    public bool ShowAll { get; private set; }

    public void OnGet()
    {
        ShowAll = Request.Query["all"] == "1";
        Entries = Watches.GetAll(WatchedItemType.Pool);
        Suggestions = Watches.GetPoolSuggestions(ShowAll);
    }

    /// <summary>"added automatically from kevinsano" for pools that came from an artist.</summary>
    public static string? OriginText(string? origin) => origin switch
    {
        null or "" => null,
        _ when origin.StartsWith("auto:") => $"added automatically: mostly {origin[5..]}'s work",
        _ when origin.StartsWith("suggested:") => $"added from a suggestion for {origin[10..]}",
        _ => null,
    };

    public IActionResult OnPostAcceptSuggestion(long poolId, string? artist)
    {
        Watches.AcceptPoolSuggestion(poolId, artist ?? "");
        return Done($"Added pool #{poolId}.");
    }

    public IActionResult OnPostIgnoreSuggestion(long artistId, long poolId)
    {
        Watches.IgnorePoolSuggestion(artistId, poolId);
        return RedirectToPage(null, null, null, "suggested");
    }

    public IActionResult OnPostAdd(string? pool)
    {
        var match = pool is null ? null : PoolReference().Match(pool.Trim());
        if (match is null || !match.Success)
            return Fail("Enter a pool ID or a pool URL.");

        var id = match.Groups["id"].Value;
        return Watches.Add(WatchedItemType.Pool, id, $"Pool #{id}") is null
            ? Fail($"Pool #{id} is already watched.")
            : Done($"Added pool #{id}.");
    }

    public IActionResult OnPostRename(long id, string? name)
    {
        Watches.SetLocalName(id, name);
        return Updated(id);
    }

    [GeneratedRegex(@"^(?:.*/pools/)?(?<id>\d+)(?:[/?#].*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex PoolReference();
}
