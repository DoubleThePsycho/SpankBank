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

    public void OnGet() => Entries = Watches.GetAll(WatchedItemType.Pool);

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
