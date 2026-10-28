using Microsoft.AspNetCore.Mvc;
using SpankBank.Core.Models;
using SpankBank.Data.Repositories;

namespace SpankBank.Web.Pages.Watches;

public class ArtistsModel : WatchPageModel
{
    public ArtistsModel(WatchesRepository watches) : base(watches) { }

    protected override WatchedItemType ItemType => WatchedItemType.Artist;
    protected override string ModalBodyPartial => "_ArtistModalBody";

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

        var id = Watches.Add(WatchedItemType.Artist, tag, tag);
        if (id is null)
            return Fail($"{tag} is already watched.");

        Watches.RefreshArtist(id.Value, tag);
        return Done($"Added artist {tag}.");
    }

    public IActionResult OnPostToggleTag(long id, long tagId, bool enabled)
    {
        Watches.SetArtistTagEnabled(tagId, enabled);
        return Updated(id);
    }

    public IActionResult OnPostRemoveTag(long id, long tagId)
    {
        Watches.DeleteArtistTag(tagId);
        return Updated(id);
    }

    public IActionResult OnPostAddTag(long id, string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
            return Fail("Enter a character tag.");

        var name = WatchesRepository.Normalize(tag);
        if (!Watches.IsCharacterTag(name))
            return Fail($"\"{name}\" is not a character tag in the catalog.");

        Watches.AddArtistTag(id, name, ArtistTagKind.Character, isManual: true);
        return Updated(id);
    }

    public IActionResult OnPostRescan(long id, string artist)
    {
        Watches.RefreshArtist(id, artist);
        return Updated(id);
    }
}
