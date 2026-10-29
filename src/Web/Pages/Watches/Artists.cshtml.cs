using Microsoft.AspNetCore.Mvc;
using SpankBank.Core.Models;
using SpankBank.Data.Repositories;
using SpankBank.Web.Services;

namespace SpankBank.Web.Pages.Watches;

public class ArtistsModel : WatchPageModel
{
    public ArtistsModel(WatchesRepository watches) : base(watches) { }

    protected override WatchedItemType ItemType => WatchedItemType.Artist;
    // Artists are managed on their own page (/artist/<name>); only the Enable/Disable link on this list posts here.
    protected override string ModalBodyPartial => throw new NotSupportedException();

    public IReadOnlyList<WatchEntry> Entries { get; private set; } = [];

    /// <summary>Set after a bulk add; the page shows it above the list.</summary>
    public BulkAddResult? Bulk { get; private set; }

    public void OnGet() => Entries = Watches.GetAll(WatchedItemType.Artist);

    public IActionResult OnPostBulk(string? names)
    {
        if (ArtistBulkAdd.ParseNames(names).Count == 0)
            return Fail("Paste at least one artist name.");

        Bulk = ArtistBulkAdd.Run(Watches, names);
        Entries = Watches.GetAll(WatchedItemType.Artist);
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
