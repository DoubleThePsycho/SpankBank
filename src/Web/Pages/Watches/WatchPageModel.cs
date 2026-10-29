using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SpankBank.Core.Models;
using SpankBank.Data.Repositories;

namespace SpankBank.Web.Pages.Watches;

/// <summary>
/// Handlers every watch subpage shares. Requests sent by watches.ts carry "X-Fetch: 1" and get the re-rendered
/// modal body back instead of a redirect, so the page updates without a reload.
/// </summary>
public abstract class WatchPageModel : PageModel
{
    protected readonly WatchesRepository Watches;

    protected WatchPageModel(WatchesRepository watches) => Watches = watches;

    protected abstract WatchedItemType ItemType { get; }

    /// <summary>Partial (model: WatchEntry) holding the part of an entry's modal that changes.</summary>
    protected abstract string ModalBodyPartial { get; }

    [TempData] public string? Message { get; set; }
    [TempData] public string? Error { get; set; }

    private bool IsFetch => Request.Headers["X-Fetch"] == "1";

    public IActionResult OnPostToggle(long id, bool enabled, string? returnUrl)
    {
        Watches.SetEnabled(id, enabled);
        if (Request.Headers["X-Fetch"] == "pill") return new NoContentResult(); // the list updates the pill in place
        // A plain post comes back to the same page, sorting and all.
        if (!IsFetch && returnUrl is not null && Url.IsLocalUrl(returnUrl)) return LocalRedirect(returnUrl);
        return Updated(id);
    }

    public async Task<IActionResult> OnPostIgnoreBlacklistAsync(long id, bool ignore, [FromServices] Web.Services.WorkerClient worker)
    {
        Watches.SetIgnoreBlacklist(id, ignore);
        await worker.NotifyAsync("downloads"); // the downloader re-applies the blacklist first
        return Updated(id);
    }

    public async Task<IActionResult> OnPostDeleteAsync(long id, string? posts)
    {
        await BeforeDeleteAsync(id, posts);
        Watches.Delete(id);
        return IsFetch ? new NoContentResult() : Done("Removed.");
    }

    /// <summary>Runs before a watch is deleted, while its matches still exist. <paramref name="posts"/> is "all", "exclusive" or empty.</summary>
    protected virtual Task BeforeDeleteAsync(long id, string? posts) => Task.CompletedTask;

    /// <summary>The entry's modal body after a change (fetch), or a redirect back to the list.</summary>
    protected IActionResult Updated(long id)
    {
        if (!IsFetch) return RedirectToPage();

        var entry = Watches.GetAll(ItemType).FirstOrDefault(e => e.Id == id);
        return entry is null ? NotFound() : Partial(ModalBodyPartial, entry);
    }

    protected IActionResult Fail(string message)
    {
        if (IsFetch) return new ContentResult { StatusCode = StatusCodes.Status400BadRequest, Content = message };

        Error = message;
        return RedirectToPage();
    }

    protected IActionResult Done(string message)
    {
        Message = message;
        return RedirectToPage();
    }
}
