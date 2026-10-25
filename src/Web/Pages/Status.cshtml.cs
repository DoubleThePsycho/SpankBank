using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SpankBank.Data;
using SpankBank.Data.Import;
using SpankBank.Data.Repositories;

namespace SpankBank.Web.Pages;

public class StatusModel : PageModel
{
    private readonly SettingsRepository _settings;
    private readonly ImportHistoryRepository _history;
    private readonly PostsRepository _posts;

    public StatusModel(SettingsRepository settings, ImportHistoryRepository history, PostsRepository posts)
    {
        _settings = settings;
        _history = history;
        _posts = posts;
    }

    public string? LastRunStartedAt { get; private set; }
    public string? LastRunCompletedAt { get; private set; }
    public string? LastRunResult { get; private set; }
    public string DailyRunTimeUtc { get; private set; } = "04:00";
    public bool RunRequested { get; private set; }
    public long PostCount { get; private set; }
    public IReadOnlyList<ImportHistoryEntry> Imports { get; private set; } = [];

    [TempData]
    public string? Message { get; set; }

    public void OnGet() => Load();

    public IActionResult OnPost()
    {
        // The worker polls this flag and starts a refresh.
        _settings.Set(WorkerSettingsKeys.RunNowRequested, "true");
        Message = "Run requested. The worker picks it up within about 15 seconds.";
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
        Imports = _history.GetRecent(10);
    }
}
