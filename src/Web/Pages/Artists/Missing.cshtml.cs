using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SpankBank.Core.Models;
using SpankBank.Data;
using SpankBank.Data.Repositories;
using SpankBank.Web.Services;

namespace SpankBank.Web.Pages.Artists;

/// <summary>The artist's lost and pending posts, with an upload box for files from the user's own PC.</summary>
public class MissingModel : PageModel
{
    private readonly WatchesRepository _watches;
    private readonly SettingsRepository _settings;

    public MissingModel(WatchesRepository watches, SettingsRepository settings)
    {
        _watches = watches;
        _settings = settings;
    }

    public string Name { get; private set; } = "";
    public string Kind { get; private set; } = WatchesRepository.MissingLost;
    public WatchPostStats Stats { get; private set; } = WatchPostStats.None;
    public WatchPage<MissingPost> Posts { get; private set; } = new([], 0, 1, 1);
    public int PageNumber { get; private set; } = 1;

    public string PageUrl(int page) => $"/artist/{Uri.EscapeDataString(Name)}/missing?kind={Kind}&page={page}";

    public IActionResult OnGet(string name, string? kind)
    {
        var watch = FindWatch(_watches, name);
        if (watch is null) return NotFound();

        Name = watch.SourceValue;
        Kind = kind == WatchesRepository.MissingPending ? WatchesRepository.MissingPending : WatchesRepository.MissingLost;
        PageNumber = int.TryParse(Request.Query["page"], out var page) ? Math.Max(1, page) : 1;
        Stats = _watches.GetPostStats(WatchedItemType.Artist, [watch.Id]).GetValueOrDefault(watch.Id, WatchPostStats.None);
        Posts = _watches.GetMissingPosts(watch.Id, Kind, PageNumber, ListView.PageSize(_settings.Get(WorkerSettingsKeys.ListPageSize)));
        return Page();
    }

    public static WatchEntry? FindWatch(WatchesRepository watches, string name)
        => watches.GetAll(WatchedItemType.Artist).FirstOrDefault(w => string.Equals(w.SourceValue, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>"1.2 MB", "340 KB"...</summary>
    public static string Size(long? bytes) => bytes switch
    {
        null => "-",
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / 1024.0 / 1024.0:0.#} MB",
    };
}
