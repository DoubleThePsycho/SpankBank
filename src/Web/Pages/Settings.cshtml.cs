using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SpankBank.Core;
using SpankBank.Data;
using SpankBank.Data.Matching;
using SpankBank.Data.Repositories;
using SpankBank.Web.Services;

namespace SpankBank.Web.Pages;

public class SettingsModel : PageModel
{
    private readonly SettingsRepository _settings;
    private readonly WatchesRepository _watches;
    private readonly WorkerClient _worker;

    public SettingsModel(SettingsRepository settings, WatchesRepository watches, WorkerClient worker)
    {
        _settings = settings;
        _watches = watches;
        _worker = worker;
    }

    [TempData] public string? Message { get; set; }
    [TempData] public string? Error { get; set; }

    public string DailyRunTimeUtc { get; private set; } = "04:00";
    public bool DownloadsEnabled { get; private set; } = true;
    public int DailyLimit { get; private set; } = 2000;
    public int Parallelism { get; private set; } = 3;
    public bool DeleteMode { get; private set; }
    public int ListPageSize { get; private set; } = WatchesRepository.DefaultListPageSize;
    public bool PoolsAutoAdd { get; private set; } = true;
    public int PoolsPercent { get; private set; } = PoolSuggester.DefaultAutoAddPercent;

    public void OnGet()
    {
        DailyRunTimeUtc = _settings.Get(WorkerSettingsKeys.DailyRunTimeUtc) ?? "04:00";
        DownloadsEnabled = _settings.Get(WorkerSettingsKeys.DownloadEnabled) != "false";
        DailyLimit = int.TryParse(_settings.Get(WorkerSettingsKeys.DownloadMaxPerDay), out var limit) ? limit : 2000;
        Parallelism = int.TryParse(_settings.Get(WorkerSettingsKeys.DownloadParallelism), out var parallel) ? Math.Clamp(parallel, 1, 64) : 3;
        DeleteMode = _settings.Get(WorkerSettingsKeys.DeleteModeEnabled) == "true";
        ListPageSize = Web.Services.ListView.PageSize(_settings.Get(WorkerSettingsKeys.ListPageSize));
        PoolsAutoAdd = _settings.Get(WorkerSettingsKeys.PoolsAutoAddEnabled) != "false";
        PoolsPercent = int.TryParse(_settings.Get(WorkerSettingsKeys.PoolsAutoAddPercent), out var percent) ? percent : PoolSuggester.DefaultAutoAddPercent;
    }

    public async Task<IActionResult> OnPostAsync(string? dailyRunTime, bool downloadsEnabled, int dailyLimit, int parallelism, bool poolsAutoAdd, int poolsPercent, bool deleteMode, int listPageSize)
    {
        if (!TimeOnly.TryParseExact(dailyRunTime, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            return Fail("The daily update time must look like 04:00.");
        if (dailyLimit < 0 || dailyLimit > 100_000)
            return Fail("The daily download limit must be between 0 and 100,000.");
        if (parallelism is < 1 or > 64)
            return Fail("Parallel downloads must be between 1 and 64.");
        if (listPageSize is < 10 or > 1000)
            return Fail("Rows per page must be between 10 and 1,000.");
        if (poolsPercent is < 1 or > 100)
            return Fail("The pool percentage must be between 1 and 100.");

        var poolsChanged =
            (_settings.Get(WorkerSettingsKeys.PoolsAutoAddEnabled) != "false") != poolsAutoAdd
            || (int.TryParse(_settings.Get(WorkerSettingsKeys.PoolsAutoAddPercent), out var old) ? old : PoolSuggester.DefaultAutoAddPercent) != poolsPercent;

        _settings.Set(WorkerSettingsKeys.DailyRunTimeUtc, dailyRunTime!);
        _settings.Set(WorkerSettingsKeys.DownloadEnabled, downloadsEnabled ? "true" : "false");
        _settings.Set(WorkerSettingsKeys.DownloadMaxPerDay, dailyLimit.ToString(CultureInfo.InvariantCulture));
        _settings.Set(WorkerSettingsKeys.DownloadParallelism, parallelism.ToString(CultureInfo.InvariantCulture));
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
