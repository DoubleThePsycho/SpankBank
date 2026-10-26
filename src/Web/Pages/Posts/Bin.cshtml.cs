using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SpankBank.Data.RubbishBin;
using SpankBank.Web.Services;

namespace SpankBank.Web.Pages.Posts;

public class BinModel : PageModel
{
    private readonly BinStore _bin;
    private readonly WorkerClient _worker;

    public BinModel(BinStore bin, WorkerClient worker)
    {
        _bin = bin;
        _worker = worker;
    }

    [TempData] public string? Message { get; set; }
    [TempData] public string? Error { get; set; }

    public IReadOnlyList<BinnedPost> Posts { get; private set; } = [];

    public void OnGet() => Posts = _bin.List();

    public async Task<IActionResult> OnPostRestoreAsync(long id)
    {
        if (!_bin.RequestRestore(id)) Error = "That post is not in the bin.";
        else
        {
            await _worker.NotifyAsync("bin");
            Message = $"Restoring post #{id}.";
        }
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostEmptyAsync()
    {
        var count = _bin.RequestEmpty();
        await _worker.NotifyAsync("bin");
        Message = count == 0 ? "The bin was already empty." : $"Removing {count:N0} post(s) for good.";
        return RedirectToPage();
    }
}
