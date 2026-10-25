using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SpankBank.Core.Models;
using SpankBank.Data.Downloads;
using SpankBank.Data.Repositories;
using SpankBank.Web.Services;

namespace SpankBank.Web.Pages.Posts;

public class ShowModel : PageModel
{
    // e621's order of the sidebar tag groups (TagCategory::SPLIT_HEADER_LIST) and their headers.
    private static readonly (int Category, string CssName, string Header)[] Groups =
    [
        (6, "invalid", "Invalid"), (1, "artist", "Artists"), (2, "contributor", "Contributors"), (3, "copyright", "Copyrights"),
        (4, "character", "Characters"), (5, "species", "Species"), (0, "general", "General"), (7, "meta", "Meta"), (8, "lore", "Lore"),
    ];

    private readonly DownloadedPostsRepository _downloaded;
    private readonly PostsRepository _catalog;
    private readonly TagsRepository _tags;

    public ShowModel(DownloadedPostsRepository downloaded, PostsRepository catalog, TagsRepository tags)
    {
        _downloaded = downloaded;
        _catalog = catalog;
        _tags = tags;
    }

    public DownloadedPostDetail Post { get; private set; } = null!;
    public PostCatalogExtras? Extras { get; private set; }
    public IReadOnlyList<(string CssName, string Header, IReadOnlyList<TagInfo> Tags)> TagGroups { get; private set; } = [];
    public IReadOnlyList<long> ChildIds { get; private set; } = [];
    public bool ParentIsLocal { get; private set; }

    /// <summary>"image", "video" or "other": how the file is shown.</summary>
    public string MediaKind { get; private set; } = "other";
    /// <summary>URL of what to show, or null when no file is on disk.</summary>
    public string? MediaUrl { get; private set; }
    public bool ShowingSample { get; private set; }

    public IActionResult OnGet(long id)
    {
        var post = _downloaded.GetPost(id);
        if (post is null) return NotFound();

        Post = post;
        Extras = _catalog.GetExtras(id);
        ChildIds = _downloaded.GetChildIds(id);
        ParentIsLocal = post.ParentId is { } parent && _downloaded.Exists(parent);
        BuildTagGroups();
        ChooseMedia();
        return Page();
    }

    private void BuildTagGroups()
    {
        var names = Post.TagString.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var known = _tags.GetTagInfo(names).ToDictionary(t => t.Name);

        var groups = new List<(string, string, IReadOnlyList<TagInfo>)>();
        foreach (var (category, cssName, header) in Groups)
        {
            var inGroup = names
                .Select(name => known.TryGetValue(name, out var info) ? info : new TagInfo(name, 0, 0)) // not in the catalog: general
                .Where(info => info.Category == category)
                .OrderBy(info => info.Name, StringComparer.Ordinal)
                .ToList();
            if (inGroup.Count > 0) groups.Add((cssName, header, inGroup));
        }
        TagGroups = groups;
    }

    private void ChooseMedia()
    {
        var md5 = Post.Md5;
        if (md5.Length != 32) return;

        var original = MediaPaths.Original(WebPaths.MediaDir, md5, Post.FileExt);
        var sample = MediaPaths.Sample(WebPaths.MediaDir, md5);
        var isVideo = Post.FileExt is "webm" or "mp4";
        var isImage = Post.FileExt is "jpg" or "jpeg" or "png" or "gif" or "webp";

        if (System.IO.File.Exists(original))
        {
            MediaUrl = MediaPaths.OriginalUrl(md5, Post.FileExt);
            MediaKind = isVideo ? "video" : isImage ? "image" : "other";
        }
        else if (System.IO.File.Exists(sample))
        {
            MediaUrl = MediaPaths.SampleUrl(md5);
            MediaKind = "image";
            ShowingSample = true;
        }
    }

    public static string CountLabel(long count) => count switch
    {
        < 1_000 => count.ToString(),
        < 10_000 => $"{count / 1_000.0:0.0}k",
        < 1_000_000 => $"{count / 1_000}k",
        < 10_000_000 => $"{count / 1_000_000.0:0.0}m",
        _ => $"{count / 1_000_000}m",
    };

    public static string HumanSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} Bytes",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024:0.#} MB",
        _ => $"{bytes / 1024.0 / 1024 / 1024:0.#} GB",
    };
}
