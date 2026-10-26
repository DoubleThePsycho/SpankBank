using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SpankBank.Data;
using SpankBank.Data.Blacklist;
using SpankBank.Web.Services;

namespace SpankBank.Web.Pages.Posts;

public class BlacklistModel : PageModel
{
    private readonly BlacklistRepository _blacklist;
    private readonly DatabasePaths _paths;
    private readonly WorkerClient _worker;

    public BlacklistModel(BlacklistRepository blacklist, DatabasePaths paths, WorkerClient worker)
    {
        _blacklist = blacklist;
        _paths = paths;
        _worker = worker;
    }

    [TempData] public string? Message { get; set; }
    [TempData] public string? Error { get; set; }

    public string Entries { get; private set; } = "";
    public int Pending { get; private set; }
    public int Local { get; private set; }

    public void OnGet() => Load();

    public async Task<IActionResult> OnPostAsync(string? entries)
    {
        var lines = (entries ?? "").Split('\n', StringSplitOptions.TrimEntries);
        if (_blacklist.SetAll(lines) is { } problem)
        {
            Error = problem;
            Entries = entries ?? "";
            Load(keepEntries: true);
            return Page();
        }

        await _worker.NotifyAsync("downloads"); // wakes the downloader, which re-applies the blacklist first
        Message = "Saved.";
        return RedirectToPage();
    }

    private void Load(bool keepEntries = false)
    {
        if (!keepEntries) Entries = string.Join('\n', _blacklist.GetAll());
        (Pending, Local) = _blacklist.CountBlocked(_paths.DeviceDatabasePath);
    }
}
