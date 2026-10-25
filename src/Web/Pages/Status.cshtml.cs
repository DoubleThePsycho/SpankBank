using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SpankBank.Data;
using SpankBank.Data.Downloads;
using SpankBank.Data.Import;
using SpankBank.Data.Repositories;
using SpankBank.Web.Services;

namespace SpankBank.Web.Pages;

public class StatusModel : PageModel
{
    private readonly SettingsRepository _settings;
    private readonly ImportHistoryRepository _history;
    private readonly PostsRepository _posts;
    private readonly DownloadedPostsRepository _downloaded;
    private readonly DownloadStore _downloads;
    private readonly WorkerClient _worker;

    public StatusModel(SettingsRepository settings, ImportHistoryRepository history, PostsRepository posts,
        DownloadedPostsRepository downloaded, DownloadStore downloads, WorkerClient worker)
    {
        _settings = settings;
        _history = history;
        _posts = posts;
        _downloaded = downloaded;
        _downloads = downloads;
        _worker = worker;
    }

    public string? LastRunStartedAt { get; private set; }
    public string? LastRunCompletedAt { get; private set; }
    public string? LastRunResult { get; private set; }
    public string DailyRunTimeUtc { get; private set; } = "04:00";
    public bool RunRequested { get; private set; }
    public long PostCount { get; private set; }
    public long DownloadedCount { get; private set; }
    public int SavedToday { get; private set; }
    public int DailyLimit { get; private set; }
    public int Waiting { get; private set; }
    public bool DownloadsPaused { get; private set; }
    public IReadOnlyList<ImportHistoryEntry> Imports { get; private set; } = [];

    [TempData]
    public string? Message { get; set; }

    public void OnGet() => Load();

    public async Task<IActionResult> OnPostAsync()
    {
        // The flag survives a worker restart; the command wakes the worker right away.
        _settings.Set(WorkerSettingsKeys.RunNowRequested, "true");
        Message = await _worker.NotifyAsync("run-export")
            ? "Started."
            : "The worker is not reachable. The refresh will start as soon as it is.";
        return RedirectToPage();
    }

    private void Load()
    {
        LastRunStartedAt = _settings.Get(WorkerSettingsKeys.LastRunStartedAt);
        LastRunCompletedAt = _settings.Get(WorkerSettingsKeys.LastRunCompletedAt);
        LastRunResult = _settings.Get(WorkerSettingsKeys.LastRunResult);
        DailyRunTimeUtc = _settings.Get(WorkerSettingsKeys.DailyRunTimeUtc) ?? "04:00";
        RunRequested = _settings.Get(WorkerSettingsKeys.RunNowRequested) == "true";
        PostCount = _posts.CountPosts();
        DownloadedCount = _downloaded.CountPosts();
        SavedToday = _downloads.SavedToday();
        DailyLimit = int.TryParse(_settings.Get(WorkerSettingsKeys.DownloadMaxPerDay), out var limit) ? limit : 2000;
        Waiting = _downloads.CountPending(DateTimeOffset.UtcNow);
        DownloadsPaused = _settings.Get(WorkerSettingsKeys.DownloadEnabled) == "false";
        Imports = _history.GetRecent(10);
    }
}
