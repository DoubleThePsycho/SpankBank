using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SpankBank.Data.Repositories;

namespace SpankBank.Web.Pages.Posts;

public class IndexModel : PageModel
{
    public const int PageSize = 75;

    private readonly PostsRepository _posts;

    public IndexModel(PostsRepository posts) => _posts = posts;

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

        // Tag search is not implemented yet: the query text is kept in the box but not applied.
        Total = _posts.CountPosts();
        LastPage = (int)Math.Max(1, (Total + PageSize - 1) / PageSize);
        if (PageNumber > LastPage) PageNumber = LastPage;
        Items = _posts.GetRecentPosts(PageSize, offset: (PageNumber - 1) * PageSize);
    }

    public string PageUrl(int page) => $"/posts?page={page}" + (string.IsNullOrWhiteSpace(Tags) ? "" : $"&tags={Uri.EscapeDataString(Tags)}");

    public static string PreviewUrl(PostListItem post)
        => post.Md5 is { Length: >= 4 } md5
            ? $"https://static1.e621.net/data/preview/{md5[..2]}/{md5[2..4]}/{md5}.jpg"
            : "/dist/assets/blank.png";

    public static string Short(int value)
    {
        var v = Math.Abs(value);
        return v >= 1000 ? $"{Math.Round(v / 1000.0, 1)}k" : v.ToString();
    }
}
