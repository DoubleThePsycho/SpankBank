using Microsoft.AspNetCore.Mvc.RazorPages;
using SpankBank.Data;
using SpankBank.Data.Downloads;
using SpankBank.Data.Repositories;

namespace SpankBank.Web.Pages.Posts;

/// <summary>What the downloader is doing and has done: live progress, counters and the posts it could not fetch.</summary>
public class DownloadsModel : PageModel
{
    private readonly DownloadedPostsRepository _downloaded;
    private readonly DownloadStore _store;
    private readonly SettingsRepository _settings;

    public DownloadsModel(DownloadedPostsRepository downloaded, DownloadStore store, SettingsRepository settings)
    {
        _downloaded = downloaded;
        _store = store;
        _settings = settings;
    }

    public long LocalFiles { get; private set; }
    public int Waiting { get; private set; }
    public int SavedToday { get; private set; }
    public int DailyLimit { get; private set; } = 2000;
    public bool Paused { get; private set; }
    public int ProblemCount { get; private set; }
    public IReadOnlyList<DownloadProblem> Problems { get; private set; } = [];

    public void OnGet()
    {
        LocalFiles = _downloaded.CountPosts();
        Waiting = _store.CountPending(DateTimeOffset.UtcNow);
        SavedToday = _store.SavedToday();
        DailyLimit = int.TryParse(_settings.Get(WorkerSettingsKeys.DownloadMaxPerDay), out var limit) ? limit : 2000;
        Paused = _settings.Get(WorkerSettingsKeys.DownloadEnabled) == "false";
        ProblemCount = _store.CountProblems();
        Problems = _store.GetProblems(50);
    }
}
