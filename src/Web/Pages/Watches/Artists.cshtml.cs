using Microsoft.AspNetCore.Mvc;
using SpankBank.Core.Models;
using SpankBank.Data;
using SpankBank.Data.Repositories;
using SpankBank.Web.Services;

namespace SpankBank.Web.Pages.Watches;

public class ArtistsModel : WatchPageModel
{
    private readonly SettingsRepository _settings;

    public ArtistsModel(WatchesRepository watches, SettingsRepository settings) : base(watches) => _settings = settings;

    protected override WatchedItemType ItemType => WatchedItemType.Artist;
    // Artists are managed on their own page (/artist/<name>); only the Enable/Disable link on this list posts here.
    protected override string ModalBodyPartial => throw new NotSupportedException();

    public WatchPage<ArtistListItem> Artists { get; private set; } = new([], 0, 1, 1);
    public ListView View { get; private set; } = new("/artists/local", null, null, 1, "name", false);

    /// <summary>Set after a bulk add; the page shows it above the list.</summary>
    public BulkAddResult? Bulk { get; private set; }

    public void OnGet(string? sort = null, string? dir = null) => Load(int.TryParse(Request.Query["page"], out var page) ? page : 1, sort, dir);

    private void Load(int page, string? sort, string? dir)
    {
        View = new ListView("/artists/local", sort, dir, page, defaultSort: "name", defaultDescending: false);
        Artists = Watches.GetArtistPage(View.Sort, View.Descending, View.Page, ListView.PageSize(_settings.Get(WorkerSettingsKeys.ListPageSize)));
    }

    public IActionResult OnPostBulk(string? names)
    {
        if (ArtistBulkAdd.ParseNames(names).Count == 0)
            return Fail("Paste at least one artist name.");

        Bulk = ArtistBulkAdd.Run(Watches, names);
        Load(1, null, null);
        return Page();
    }

    public IActionResult OnPostAdd(string? artist)
    {
        if (string.IsNullOrWhiteSpace(artist))
            return Fail("Enter an artist tag.");

        var tag = Watches.ResolveArtistTag(artist);
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
