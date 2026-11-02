using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SpankBank.Core.Models;
using SpankBank.Data.Downloads;
using SpankBank.Data.Repositories;
using SpankBank.Web.Services;

namespace SpankBank.Web.Pages.Posts;

public class ShowModel : PageModel
{
    private readonly DownloadedPostsRepository _downloaded;
    private readonly PostsRepository _catalog;
    private readonly TagsRepository _tags;
    private readonly DownloadedPostSearch _search;
    private readonly FavoritesRepository _favorites;

    public ShowModel(DownloadedPostsRepository downloaded, PostsRepository catalog, TagsRepository tags, DownloadedPostSearch search, FavoritesRepository favorites)
    {
        _favorites = favorites;
        _search = search;
        _downloaded = downloaded;
        _catalog = catalog;
        _tags = tags;
    }

    public DownloadedPostDetail Post { get; private set; } = null!;
    public PostCatalogExtras? Extras { get; private set; }
    public IReadOnlyList<(string CssName, string Header, IReadOnlyList<TagInfo> Tags)> TagGroups { get; private set; } = [];
    public IReadOnlyList<long> ChildIds { get; private set; } = [];
    public bool ParentIsLocal { get; private set; }
    public IReadOnlyList<PoolNavItem> PoolNav { get; private set; } = [];
    /// <summary>Url of the search this post was reached from with a tag added, or excluded with a leading "-".</summary>
    public string SearchUrl(string tag) => "/posts?tags=" + Uri.EscapeDataString(((Query ?? "").Trim() + " " + tag).Trim());

    public bool IsFavorite { get; private set; }
    /// <summary>The search this post was reached from (?q=), shown in the search box to build on.</summary>
    public string? Query { get; private set; }
    /// <summary>The parent and children that are on this device, shown as thumbnails under the relationship notices.</summary>
    public IReadOnlyList<PostListItem> ParentPreview { get; private set; } = [];
    public IReadOnlyList<PostListItem> ChildPreview { get; private set; } = [];
    /// <summary>URL of the downsized sample when the original is shown and a sample is on disk too (for the size selector).</summary>
    public string? SampleUrl { get; private set; }

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
        PoolNav = _search.GetPoolNavigation(id, long.TryParse(Request.Query.ContainsKey("pool_id") ? Request.Query["pool_id"] : Request.Query["pool"], out var poolId) ? poolId : null);
        Query = Request.Query["q"].ToString() is { Length: > 0 } q ? q : null;
        IsFavorite = _favorites.IsFavorite(id);
        ParentPreview = ParentIsLocal ? _search.GetPosts([post.ParentId!.Value]) : [];
        ChildPreview = _search.GetPosts(ChildIds).OrderBy(p => p.Id).ToList();
        BuildTagGroups();
        ChooseMedia();
        return Page();
    }

    private void BuildTagGroups()
    {
        var names = Post.TagString.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var known = _tags.GetTagInfo(names).ToDictionary(t => t.Name);

        var groups = new List<(string, string, IReadOnlyList<TagInfo>)>();
        foreach (var (category, cssName, header) in TagCategories.SidebarOrder)
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
            MediaKind = isVideo ? "video" : isImage ? "image" : Post.FileExt == "swf" ? "flash" : "other";
            if (isImage && System.IO.File.Exists(sample)) SampleUrl = MediaPaths.SampleUrl(md5);
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
