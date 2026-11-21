using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SpankBank.Data;
using SpankBank.Data.Blacklist;
using SpankBank.Data.Downloads;
using SpankBank.Data.Matching;
using SpankBank.Data.Repositories;
using SpankBank.Web.Services;

namespace SpankBank.Web.Pages;

/// <summary>
/// The settings page: one form with a section per topic (Downloads, Library, Blacklist, Theme), shown one at a time from
/// <c>?tab=</c> and searchable across sections, like e621ng's settings page. Everything is saved together.
/// </summary>
public class SettingsModel : PageModel
{
    public static readonly IReadOnlyList<(string Key, string Label)> Sections =
        [("downloads", "Downloads"), ("library", "Library"), ("blacklist", "Blacklist"), ("theme", "Theme")];

    private readonly SettingsRepository _settings;
    private readonly WatchesRepository _watches;
    private readonly WorkerClient _worker;
    private readonly DownloadStore _downloads;
    private readonly BlacklistRepository _blacklist;
    private readonly DatabasePaths _paths;
    private readonly ThemeSettings _theme;

    public SettingsModel(SettingsRepository settings, WatchesRepository watches, WorkerClient worker, DownloadStore downloads,
        BlacklistRepository blacklist, DatabasePaths paths, ThemeSettings theme)
    {
        _settings = settings;
        _watches = watches;
        _worker = worker;
        _downloads = downloads;
        _blacklist = blacklist;
        _paths = paths;
        _theme = theme;
    }

    [TempData] public string? Message { get; set; }
    [TempData] public string? Error { get; set; }

    /// <summary>The section shown (the first one when <c>?tab=</c> is missing or unknown).</summary>
    public string Tab { get; private set; } = Sections[0].Key;
    public string Find { get; private set; } = "";

    // Downloads
    public string DailyRunTimeUtc { get; private set; } = "04:00";
    public bool DownloadsEnabled { get; private set; } = true;
    public int DailyLimit { get; private set; } = 2000;
    public int Parallelism { get; private set; } = 3;
    public bool SkipInferiorDeleted { get; private set; }
    public (int Inferior, int LowEffortEdits) Recognised { get; private set; }

    // Library
    public int ListPageSize { get; private set; } = WatchesRepository.DefaultListPageSize;
    public bool PoolsAutoAdd { get; private set; } = true;
    public int PoolsPercent { get; private set; } = PoolSuggester.DefaultAutoAddPercent;
    public bool DeleteMode { get; private set; }

    // Blacklist
    public string Entries { get; private set; } = "";
    public int BlockedPending { get; private set; }
    public int BlockedLocal { get; private set; }

    // Theme
    public string Theme { get; private set; } = ThemeSettings.DefaultTheme;
    public string Extra { get; private set; } = ThemeSettings.DefaultExtra;

    public void OnGet(string? tab, string? find)
    {
        Tab = NormalizeTab(tab);
        Find = (find ?? "").Trim();
        LoadSaved();
        LoadInfo();
    }

    public async Task<IActionResult> OnPostAsync(string? tab, string? dailyRunTime, bool downloadsEnabled, int dailyLimit, int parallelism,
        bool skipInferiorDeleted, int listPageSize, bool poolsAutoAdd, int poolsPercent, bool deleteMode, string? entries, string? theme, string? extra)
    {
        Tab = NormalizeTab(tab);
        var lines = (entries ?? "").Split('\n', StringSplitOptions.TrimEntries);

        // What was typed stays on the page when something is refused, on the section the problem is in.
        string? problem = null;
        if (!TimeOnly.TryParseExact(dailyRunTime, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            (problem, Tab) = ("The daily update time must look like 04:00.", "downloads");
        else if (dailyLimit < 0 || dailyLimit > 100_000)
            (problem, Tab) = ("The daily download limit must be between 0 and 100,000.", "downloads");
        else if (parallelism is < 1 or > 64)
            (problem, Tab) = ("Parallel downloads must be between 1 and 64.", "downloads");
        else if (listPageSize is < 10 or > 1000)
            (problem, Tab) = ("Rows per page must be between 10 and 1,000.", "library");
        else if (poolsPercent is < 1 or > 100)
            (problem, Tab) = ("The pool percentage must be between 1 and 100.", "library");
        else if (BlacklistRepository.Normalize(lines, out _) is { } blacklistProblem)
            (problem, Tab) = (blacklistProblem, "blacklist");
        else if (!ThemeSettings.IsTheme(theme) || !ThemeSettings.IsExtra(extra))
            (problem, Tab) = ("Pick one of the listed themes and embellishments.", "theme");

        if (problem is not null)
        {
            Error = problem;
            DailyRunTimeUtc = dailyRunTime ?? "";
            DownloadsEnabled = downloadsEnabled;
            DailyLimit = dailyLimit;
            Parallelism = parallelism;
            SkipInferiorDeleted = skipInferiorDeleted;
            ListPageSize = listPageSize;
            PoolsAutoAdd = poolsAutoAdd;
            PoolsPercent = poolsPercent;
            DeleteMode = deleteMode;
            Entries = entries ?? "";
            Theme = theme ?? ThemeSettings.DefaultTheme;
            Extra = extra ?? ThemeSettings.DefaultExtra;
            LoadInfo();
            return Page();
        }

        var poolsChanged =
            (_settings.Get(WorkerSettingsKeys.PoolsAutoAddEnabled) != "false") != poolsAutoAdd
            || (int.TryParse(_settings.Get(WorkerSettingsKeys.PoolsAutoAddPercent), out var old) ? old : PoolSuggester.DefaultAutoAddPercent) != poolsPercent;
        BlacklistRepository.Normalize(lines, out var normalised);
        var blacklistChanged = !_blacklist.GetAll().SequenceEqual(normalised);

        _settings.Set(WorkerSettingsKeys.DailyRunTimeUtc, dailyRunTime!);
        _settings.Set(WorkerSettingsKeys.DownloadEnabled, downloadsEnabled ? "true" : "false");
        _settings.Set(WorkerSettingsKeys.DownloadMaxPerDay, dailyLimit.ToString(CultureInfo.InvariantCulture));
        _settings.Set(WorkerSettingsKeys.DownloadParallelism, parallelism.ToString(CultureInfo.InvariantCulture));
        _settings.Set(WorkerSettingsKeys.DownloadSkipInferiorDeleted, skipInferiorDeleted ? "true" : "false");
        _settings.Set(WorkerSettingsKeys.ListPageSize, listPageSize.ToString(CultureInfo.InvariantCulture));
        _settings.Set(WorkerSettingsKeys.DeleteModeEnabled, deleteMode ? "true" : "false");
        _settings.Set(WorkerSettingsKeys.PoolsAutoAddEnabled, poolsAutoAdd ? "true" : "false");
        _settings.Set(WorkerSettingsKeys.PoolsAutoAddPercent, poolsPercent.ToString(CultureInfo.InvariantCulture));
        _theme.Save(theme!, extra!);
        if (blacklistChanged) _blacklist.SetAll(lines); // marks the blocked posts for recomputing

        // A new pool threshold needs the artists' pools worked out again; the other settings are read when they are used.
        if (poolsChanged) _watches.RecomputeArtistPools();

        await _worker.NotifyAsync("settings");
        if (blacklistChanged) await _worker.NotifyAsync("downloads"); // wakes the downloader, which re-applies the blacklist first
        Message = poolsChanged ? "Saved. Your artists' pools are being checked again." : "Saved.";
        return RedirectToPage(new { tab = Tab == Sections[0].Key ? null : Tab });
    }

    private void LoadSaved()
    {
        DailyRunTimeUtc = _settings.Get(WorkerSettingsKeys.DailyRunTimeUtc) ?? "04:00";
        DownloadsEnabled = _settings.Get(WorkerSettingsKeys.DownloadEnabled) != "false";
        DailyLimit = int.TryParse(_settings.Get(WorkerSettingsKeys.DownloadMaxPerDay), out var limit) ? limit : 2000;
        Parallelism = int.TryParse(_settings.Get(WorkerSettingsKeys.DownloadParallelism), out var parallel) ? Math.Clamp(parallel, 1, 64) : 3;
        SkipInferiorDeleted = _settings.Get(WorkerSettingsKeys.DownloadSkipInferiorDeleted) == "true";

        DeleteMode = _settings.Get(WorkerSettingsKeys.DeleteModeEnabled) == "true";
        ListPageSize = ListView.PageSize(_settings.Get(WorkerSettingsKeys.ListPageSize));
        PoolsAutoAdd = _settings.Get(WorkerSettingsKeys.PoolsAutoAddEnabled) != "false";
        PoolsPercent = int.TryParse(_settings.Get(WorkerSettingsKeys.PoolsAutoAddPercent), out var percent) ? percent : PoolSuggester.DefaultAutoAddPercent;

        Entries = string.Join('\n', _blacklist.GetAll());
        Theme = _theme.Theme;
        Extra = _theme.Extra;
    }

    /// <summary>Counts shown in the hints (not part of the form).</summary>
    private void LoadInfo()
    {
        Recognised = _downloads.CountRecognisedDeletions();
        (BlockedPending, BlockedLocal) = _blacklist.CountBlocked(_paths.DeviceDatabasePath);
    }

    private static string NormalizeTab(string? tab) => Sections.Any(s => s.Key == tab) ? tab! : Sections[0].Key;
}
