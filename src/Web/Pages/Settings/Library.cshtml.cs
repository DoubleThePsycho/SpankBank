using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SpankBank.Data;
using SpankBank.Data.Matching;
using SpankBank.Data.Repositories;
using SpankBank.Web.Services;

namespace SpankBank.Web.Pages.SettingsPages;

/// <summary>Settings &gt; Library: list sizes, automatic pools and delete mode.</summary>
public class LibraryModel : PageModel
{
    private readonly SettingsRepository _settings;
    private readonly WatchesRepository _watches;
    private readonly WorkerClient _worker;

    public LibraryModel(SettingsRepository settings, WatchesRepository watches, WorkerClient worker)
    {
        _settings = settings;
        _watches = watches;
        _worker = worker;
    }

    [TempData] public string? Message { get; set; }
    [TempData] public string? Error { get; set; }

    public bool DeleteMode { get; private set; }
    public int ListPageSize { get; private set; } = WatchesRepository.DefaultListPageSize;
    public bool PoolsAutoAdd { get; private set; } = true;
    public int PoolsPercent { get; private set; } = PoolSuggester.DefaultAutoAddPercent;

    public void OnGet()
    {
        DeleteMode = _settings.Get(WorkerSettingsKeys.DeleteModeEnabled) == "true";
        ListPageSize = ListView.PageSize(_settings.Get(WorkerSettingsKeys.ListPageSize));
        PoolsAutoAdd = _settings.Get(WorkerSettingsKeys.PoolsAutoAddEnabled) != "false";
        PoolsPercent = int.TryParse(_settings.Get(WorkerSettingsKeys.PoolsAutoAddPercent), out var percent) ? percent : PoolSuggester.DefaultAutoAddPercent;
    }

    public async Task<IActionResult> OnPostAsync(bool poolsAutoAdd, int poolsPercent, bool deleteMode, int listPageSize)
    {
        if (listPageSize is < 10 or > 1000)
            return Fail("Rows per page must be between 10 and 1,000.");
        if (poolsPercent is < 1 or > 100)
            return Fail("The pool percentage must be between 1 and 100.");

        var poolsChanged =
            (_settings.Get(WorkerSettingsKeys.PoolsAutoAddEnabled) != "false") != poolsAutoAdd
            || (int.TryParse(_settings.Get(WorkerSettingsKeys.PoolsAutoAddPercent), out var old) ? old : PoolSuggester.DefaultAutoAddPercent) != poolsPercent;

        _settings.Set(WorkerSettingsKeys.ListPageSize, listPageSize.ToString(CultureInfo.InvariantCulture));
        _settings.Set(WorkerSettingsKeys.DeleteModeEnabled, deleteMode ? "true" : "false");
        _settings.Set(WorkerSettingsKeys.PoolsAutoAddEnabled, poolsAutoAdd ? "true" : "false");
        _settings.Set(WorkerSettingsKeys.PoolsAutoAddPercent, poolsPercent.ToString(CultureInfo.InvariantCulture));

        // A new pool threshold needs the artists' pools worked out again; the other settings are read when they are used.
        if (poolsChanged) _watches.RecomputeArtistPools();

        await _worker.NotifyAsync("settings");
        Message = poolsChanged ? "Saved. Your artists' pools are being checked again." : "Saved.";
        return RedirectToPage();
    }

    private IActionResult Fail(string message)
    {
        Error = message;
        return RedirectToPage();
    }
}
