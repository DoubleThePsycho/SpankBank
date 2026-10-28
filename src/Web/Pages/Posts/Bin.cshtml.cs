using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SpankBank.Data.Repositories;
using SpankBank.Data.RubbishBin;
using SpankBank.Web.Services;

namespace SpankBank.Web.Pages.Posts;

public class BinModel : PageModel
{
    private readonly BinStore _bin;
    private readonly StrayPosts _strays;
    private readonly WorkerClient _worker;

    public BinModel(BinStore bin, StrayPosts strays, WorkerClient worker)
    {
        _strays = strays;
        _bin = bin;
        _worker = worker;
    }

    [TempData] public string? Message { get; set; }
    [TempData] public string? Error { get; set; }

    public IReadOnlyList<BinnedPost> Posts { get; private set; } = [];
    public int StrayCount { get; private set; }

    public void OnGet()
    {
        Posts = _bin.List();
        StrayCount = _strays.Count();
    }

    public async Task<IActionResult> OnPostBinStraysAsync()
    {
        var count = _strays.BinAll();
        if (count > 0) await _worker.NotifyAsync("bin");
        Message = count == 0 ? "There are no stray posts." : $"Moved {count:N0} stray post(s) to the bin.";
        return RedirectToPage();
    }

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
