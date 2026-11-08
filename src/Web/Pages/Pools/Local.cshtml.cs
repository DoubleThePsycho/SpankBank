using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using SpankBank.Core.Models;
using SpankBank.Data;
using SpankBank.Data.Repositories;
using SpankBank.Web.Pages.Watches;
using SpankBank.Web.Services;

namespace SpankBank.Web.Pages.Pools;

public partial class LocalModel : WatchPageModel
{
    private readonly SettingsRepository _settings;

    public LocalModel(WatchesRepository watches, SettingsRepository settings) : base(watches) => _settings = settings;

    protected override WatchedItemType ItemType => WatchedItemType.Pool;
    protected override string ModalBodyPartial => "_PoolModalBody";

    public WatchPage<WatchedPoolItem> Pools { get; private set; } = new([], 0, 1, 1);
    public ListView View { get; private set; } = new("/pools/local", null, null, 1, "id", true);
    public bool ShowDisabled { get; private set; }
    public int DisabledCount { get; private set; }
    public IReadOnlyList<PoolSuggestion> Suggestions { get; private set; } = [];
    public bool ShowAll { get; private set; }

    // The folded search form: pool name (or id) and the artist a pool was added for.
    public string? Name { get; private set; }
    public string? Artist { get; private set; }
    public bool SearchActive => !string.IsNullOrWhiteSpace(Name) || !string.IsNullOrWhiteSpace(Artist);

    public void OnGet(string? sort = null, string? dir = null, string? show = null)
    {
        ShowAll = Request.Query["all"] == "1";
        ShowDisabled = show == "disabled";
        Name = Request.Query["name"].ToString().Trim();
        Artist = Request.Query["artist"].ToString().Trim();
        var extra = (ShowDisabled ? "&show=disabled" : "") + (string.IsNullOrEmpty(Name) ? "" : "&name=" + Uri.EscapeDataString(Name)) + (string.IsNullOrEmpty(Artist) ? "" : "&artist=" + Uri.EscapeDataString(Artist));
        View = new ListView("/pools/local", sort, dir, int.TryParse(Request.Query["page"], out var page) ? page : 1, defaultSort: "id", defaultDescending: true, extraQuery: extra);
        Pools = Watches.GetPoolPage(View.Sort, View.Descending, View.Page, ListView.PageSize(_settings.Get(WorkerSettingsKeys.ListPageSize)), ShowDisabled, Name, Artist);
        DisabledCount = Watches.CountDisabledPools();
        Suggestions = Watches.GetPoolSuggestions(ShowAll);
    }

    /// <summary>The artist a pool was added for ("auto:name" or "suggested:name"), else null.</summary>
    public static string? OriginArtist(string? origin) => origin switch
    {
        _ when origin is not null && origin.StartsWith("auto:") && origin.Length > 5 => origin[5..],
        _ when origin is not null && origin.StartsWith("suggested:") && origin.Length > 10 => origin[10..],
        _ => null,
    };

    /// <summary>"added automatically: mostly name's work" for the pool's details (the listing shows only the artist).</summary>
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
