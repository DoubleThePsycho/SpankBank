using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SpankBank.Data.Downloads;
using SpankBank.Data.Repositories;
using SpankBank.Web.Services;

namespace SpankBank.Web.Pages.Posts;

public class IndexModel : PageModel
{
    public const int PageSize = 75;
    private const int SidebarTagLimit = 25;

    private readonly DownloadedPostSearch _search;
    private readonly TagsRepository _tags;

    public IndexModel(DownloadedPostSearch search, TagsRepository tags, FavoriteFolders folders, CatalogBrowser catalog)
    {
        _catalog = catalog;
        _folders = folders;
        _search = search;
        _tags = tags;
    }

    [BindProperty(SupportsGet = true, Name = "tags")]
    public string? Tags { get; set; }

    // Read from the query string by hand: Razor Pages reserves the route value "page", so model binding cannot use it.
    public int PageNumber { get; private set; } = 1;

    /// <summary>True on /favorites: the same listing, narrowed to favorites and newest-favorited first.</summary>
    public bool IsFavorites => Request.Path.StartsWithSegments("/favorites");
    public string BasePath => IsFavorites ? "/favorites" : "/posts";

    private readonly FavoriteFolders _folders;
    private readonly CatalogBrowser _catalog;

    /// <summary>The artist when the search is just their tag.</summary>
    public ArtistRow? Artist { get; private set; }

    /// <summary>What post links carry as ?q=, so a post page can build on this search. Nothing for the plain listing.</summary>
    public string? PostQuery => string.IsNullOrWhiteSpace(Tags) ? null : (IsFavorites ? "fav:true " : "") + Tags.Trim();

    /// <summary>The folder being browsed on /favorites; null at the top level. Folders only show while not searching.</summary>
    public FavoriteFolder? CurrentFolder { get; private set; }
    public bool FolderView => IsFavorites && string.IsNullOrWhiteSpace(Tags);
    public IReadOnlyList<FavoriteFolder> ChildFolders { get; private set; } = [];

    public IReadOnlyList<PostListItem> Items { get; private set; } = [];
    public long Total { get; private set; }
    public int LastPage { get; private set; } = 1;
    public int PageSizeUsed { get; private set; } = PageSize;
    public string? SearchError { get; private set; }

    /// <summary>The most common tags among the posts on this page, for the sidebar.</summary>
    public IReadOnlyList<(string CssName, string Header, IReadOnlyList<TagInfo> Tags)> SidebarGroups { get; private set; } = [];

    public void OnGet()
    {
        PageNumber = int.TryParse(Request.Query["page"], out var requested) && requested > 0 ? requested : 1;

        if (FolderView)
        {
            CurrentFolder = long.TryParse(Request.Query["folder_id"], out var folderId) ? _folders.Get(folderId) : null;
            if (PageNumber == 1) ChildFolders = _folders.Children(CurrentFolder?.Id);
        }
        var searchText = SearchText();
        var result = _search.Search(searchText, PageNumber, PageSize);
        PageSizeUsed = result.PageSize;
        SearchError = result.Error;
        Total = result.Total;
        LastPage = (int)Math.Max(1, (Total + PageSizeUsed - 1) / PageSizeUsed);

        if (PageNumber > LastPage)
        {
            PageNumber = LastPage;
            result = _search.Search(searchText, PageNumber, PageSize);
        }
        Items = result.Items;

        BuildSidebar();

        var single = Tags?.Trim();
        if (!string.IsNullOrEmpty(single) && single.IndexOfAny([' ', '-', '~', ':', '*']) < 0)
            Artist = _catalog.GetArtist(single.ToLowerInvariant());
    }

    // What is searched: on /favorites the typed tags plus "fav:true", and "order:favorited" unless the user chose an order.
    private string? SearchText()
    {
        if (!IsFavorites) return Tags;
        if (FolderView) return $"fav:true folder:{(CurrentFolder is { } folder ? folder.Id.ToString() : "none")} order:favorited";
        var text = "fav:true " + Tags;
        return text.Contains("order:", StringComparison.OrdinalIgnoreCase) ? text : text + " order:favorited";
    }

    private void BuildSidebar()
    {
        if (Items.Count == 0) return;

        var frequencies = _search.TagFrequencies(Items.Select(i => i.Id).ToList());
        var info = _tags.GetTagInfo(frequencies.Keys.ToList()).ToDictionary(t => t.Name);

        var top = frequencies
            .Select(f => (Name: f.Key, Count: f.Value, Info: info.TryGetValue(f.Key, out var t) ? t : new TagInfo(f.Key, 0, 0)))
            .OrderByDescending(t => t.Count)
            .ThenByDescending(t => t.Info.PostCount)
            .ThenBy(t => t.Name, StringComparer.Ordinal)
            .Take(SidebarTagLimit)
            .ToList();

        // Grouped under headers like the post page; inside a group the most common tag comes first.
        SidebarGroups = TagCategories.SidebarOrder
            .Select(g => (g.CssName, g.Header, (IReadOnlyList<TagInfo>)top.Where(t => t.Info.Category == g.Category).Select(t => t.Info).ToList()))
            .Where(g => g.Item3.Count > 0)
            .ToList();
    }

    /// <summary>Url of a page of this search; page 0 is the fast-travel placeholder the paginator script fills in.</summary>
    public string PageUrl(int page) => $"{BasePath}?page={page}" + (CurrentFolder is { } folder ? $"&folder_id={folder.Id}" : "") + (string.IsNullOrWhiteSpace(Tags) ? "" : $"&tags={Uri.EscapeDataString(Tags)}");

    /// <summary>Url of this search with a tag added, or excluded with a leading "-".</summary>
    public string TagUrl(string tag) => BasePath + "?tags=" + Uri.EscapeDataString(((Tags ?? "").Trim() + " " + tag).Trim());

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
