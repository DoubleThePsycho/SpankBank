using Microsoft.AspNetCore.Mvc;
using SpankBank.Core.Models;
using SpankBank.Data;
using SpankBank.Data.Repositories;
using SpankBank.Web.Pages.Watches;
using SpankBank.Web.Services;

namespace SpankBank.Web.Pages.Artists;

public class LocalModel : WatchPageModel
{
    private readonly SettingsRepository _settings;

    public LocalModel(WatchesRepository watches, SettingsRepository settings) : base(watches) => _settings = settings;

    protected override WatchedItemType ItemType => WatchedItemType.Artist;
    // Artists are managed on their own page (/artist/<name>); only the Enable/Disable link on this list posts here.
    protected override string ModalBodyPartial => throw new NotSupportedException();

    public WatchPage<ArtistListItem> Artists { get; private set; } = new([], 0, 1, 1);
    public ListView View { get; private set; } = new("/artists/local", null, null, 1, "name", false);

    /// <summary>Set after a bulk add; the page shows it above the list.</summary>
    public BulkAddResult? Bulk { get; private set; }

    public void OnGet(string? sort = null, string? dir = null) => Load(int.TryParse(Request.Query["page"], out var page) ? page : 1, sort, dir);

    // The folded search form: name (or alias) and enabled/disabled.
    public string? Name { get; private set; }
    public string? Status { get; private set; }
    public bool SearchActive => !string.IsNullOrWhiteSpace(Name) || Status is not null;

    private void Load(int page, string? sort, string? dir)
    {
        Name = Request.Query["name"].ToString().Trim();
        Status = Request.Query["status"].ToString() is "enabled" or "disabled" ? Request.Query["status"].ToString() : null;
        var extra = (string.IsNullOrEmpty(Name) ? "" : "&name=" + Uri.EscapeDataString(Name)) + (Status is null ? "" : "&status=" + Status);
        View = new ListView("/artists/local", sort, dir, page, defaultSort: "name", defaultDescending: false, extraQuery: extra);
        Artists = Watches.GetArtistPage(View.Sort, View.Descending, View.Page, ListView.PageSize(_settings.Get(WorkerSettingsKeys.ListPageSize)),
            Name, Status is null ? null : Status == "enabled");
    }

    /// <summary>
    /// One name: add the artist disabled and open their page. Several names (one per line, or comma separated): add them all
    /// enabled and show what was added, what was already watched and which names are not artists.
    /// </summary>
    public IActionResult OnPostAdd(string? names)
    {
        var parsed = ArtistBulkAdd.ParseNames(names);
        if (parsed.Count == 0)
            return Fail("Enter at least one artist name.");

        if (parsed.Count > 1)
        {
            Bulk = ArtistBulkAdd.Run(Watches, names);
            Load(1, null, null);
            return Page();
        }

        var tag = Watches.ResolveArtistTag(parsed[0]);
        if (NonArtistTags.Contains(tag))
            return Fail($"\"{tag}\" is a marker tag, not an artist, so it cannot be watched as one.");
        if (!Watches.IsArtistTag(tag))
            return Fail($"\"{tag}\" is not an artist tag in the catalog.");

        // Added disabled: nothing is matched or downloaded until the artist is enabled on their page.
        var id = Watches.Add(WatchedItemType.Artist, tag, tag, enabled: false);
        if (id is null)
            return Fail($"{tag} is already watched.");

        Watches.RefreshArtist(id.Value, tag);
        TempData["Message"] = $"Added {tag}, not enabled yet. Adjust the characters and filters below, then press Enable.";
        return Redirect($"/artist/{Uri.EscapeDataString(tag)}");
    }
}
