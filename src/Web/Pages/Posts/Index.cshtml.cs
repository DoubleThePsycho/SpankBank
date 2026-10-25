using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SpankBank.Data.Downloads;
using SpankBank.Data.Repositories;
using SpankBank.Web.Services;

namespace SpankBank.Web.Pages.Posts;

public class IndexModel : PageModel
{
    public const int PageSize = 75;

    private readonly DownloadedPostsRepository _posts;

    public IndexModel(DownloadedPostsRepository posts) => _posts = posts;

    [BindProperty(SupportsGet = true, Name = "tags")]
    public string? Tags { get; set; }

    [BindProperty(SupportsGet = true, Name = "page")]
    public int PageNumber { get; set; } = 1;

    public IReadOnlyList<PostListItem> Items { get; private set; } = [];
    public long Total { get; private set; }
    public int LastPage { get; private set; } = 1;

    public void OnGet()
    {
        if (PageNumber < 1) PageNumber = 1;

        // Lists downloaded posts only. Tag search is not implemented yet: the query text is kept in the box but not applied.
        Total = _posts.CountPosts();
        LastPage = (int)Math.Max(1, (Total + PageSize - 1) / PageSize);
        if (PageNumber > LastPage) PageNumber = LastPage;
        Items = _posts.GetRecentlyDownloaded(PageSize, offset: (PageNumber - 1) * PageSize);
    }

    public string PageUrl(int page) => $"/posts?page={page}" + (string.IsNullOrWhiteSpace(Tags) ? "" : $"&tags={Uri.EscapeDataString(Tags)}");

    /// <summary>The best local picture for a post: its preview, else the recovered sample, else the image itself.</summary>
    public static string PreviewUrl(PostListItem post)
    {
        if (post.Md5 is not { Length: 32 } md5)
            return "/dist/assets/blank.png";

        if (System.IO.File.Exists(MediaPaths.Preview(WebPaths.MediaDir, md5))) return MediaPaths.PreviewUrl(md5);
        if (System.IO.File.Exists(MediaPaths.Sample(WebPaths.MediaDir, md5))) return MediaPaths.SampleUrl(md5);
        if (post.FileExt is "jpg" or "png" or "gif" or "webp" && System.IO.File.Exists(MediaPaths.Original(WebPaths.MediaDir, md5, post.FileExt)))
            return MediaPaths.OriginalUrl(md5, post.FileExt);

        return "/dist/assets/blank.png";
    }

    public static string Short(int value)
    {
        var v = Math.Abs(value);
        return v >= 1000 ? $"{Math.Round(v / 1000.0, 1)}k" : v.ToString();
    }
}
