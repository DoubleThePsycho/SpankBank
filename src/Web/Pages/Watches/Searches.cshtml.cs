using Microsoft.AspNetCore.Mvc;
using SpankBank.Core.Models;
using SpankBank.Data.Repositories;

namespace SpankBank.Web.Pages.Watches;

public class SearchesModel : WatchPageModel
{
    public SearchesModel(WatchesRepository watches) : base(watches) { }

    protected override WatchedItemType ItemType => WatchedItemType.Search;
    protected override string ModalBodyPartial => "_SearchModalBody";

    public IReadOnlyList<WatchEntry> Entries { get; private set; } = [];

    public void OnGet() => Entries = Watches.GetAll(WatchedItemType.Search);

    public IActionResult OnPostAdd(string? query)
    {
        var text = string.Join(' ', (query ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (text.Length == 0)
            return Fail("Enter a search.");

        return Watches.Add(WatchedItemType.Search, text, text) is null
            ? Fail("That search is already watched.")
            : Done("Added search.");
    }
}
