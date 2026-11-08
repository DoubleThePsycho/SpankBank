using SpankBank.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.FileProviders;
using SpankBank.Data.Downloads;
using SpankBank.Data.Import;
using SpankBank.Data.Repositories;
using SpankBank.Web.Services;

var builder = WebApplication.CreateBuilder(args);

var paths = WebPaths.Databases();
DatabaseInitializer.Initialize(paths);

// Keep the key ring outside the container, or every redeploy invalidates the antiforgery tokens of open pages.
Directory.CreateDirectory(WebPaths.KeysDir);
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(WebPaths.KeysDir))
    .SetApplicationName("SpankBank");
builder.Services.AddRazorPages(options => options.Conventions.AddPageRoute("/Posts/Index", "/posts/favorites")) // the listing, narrowed to favorites
    .AddMvcOptions(options => options.Filters.Add<WatchChangeNotifier>());
builder.Services.AddHttpClient<WorkerClient>(client =>
{
    // The worker's command server; only reachable on the compose network.
    client.BaseAddress = new Uri(Environment.GetEnvironmentVariable("WORKER_URL") ?? "http://worker:8081/");
    client.Timeout = TimeSpan.FromSeconds(3);
});
builder.Services.AddSingleton(paths);
builder.Services.AddSingleton(new PostsRepository(paths.ServerDatabasePath));
builder.Services.AddSingleton(new DownloadedPostSearch(paths));
builder.Services.AddSingleton(new LocalTagCounts(paths));
builder.Services.AddSingleton(new CatalogBrowser(paths.ServerDatabasePath));
builder.Services.AddSingleton(new DownloadedPostsRepository(paths.DeviceDatabasePath));
builder.Services.AddSingleton(new TagsRepository(paths.ServerDatabasePath));
var watchesRepository = new WatchesRepository(paths.AppDatabasePath, paths.ServerDatabasePath);
builder.Services.AddSingleton(watchesRepository);
builder.Services.AddSingleton(new DownloadStore(paths));
builder.Services.AddSingleton(new SettingsRepository(paths.AppDatabasePath));
builder.Services.AddSingleton(new FavoritesRepository(paths.AppDatabasePath));
builder.Services.AddSingleton(new SpankBank.Data.Blacklist.BlacklistRepository(paths.AppDatabasePath));
var binStore = new SpankBank.Data.RubbishBin.BinStore(paths);
builder.Services.AddSingleton(binStore);
builder.Services.AddSingleton(new ArtistPostRemoval(paths, binStore));
builder.Services.AddSingleton(new StrayPosts(paths, binStore));
builder.Services.AddSingleton(new FavoriteFolders(paths.AppDatabasePath));
builder.Services.AddSingleton(new ImportHistoryRepository(paths.AppDatabasePath));

var app = builder.Build();

// Marker tags (unknown_artist...) are not artists; drop any watch on one that was added before they were excluded.
var removedMarkers = watchesRepository.RemoveNonArtistWatches();
if (removedMarkers.Count > 0)
    app.Logger.LogWarning("Removed watches on marker tags that are not artists: {Tags}", string.Join(", ", removedMarkers));
var removedMarkerPools = watchesRepository.RemovePoolsOfNonArtists();
if (removedMarkerPools > 0)
    app.Logger.LogWarning("Removed {Count} pool watches that were added for marker tags treated as artists.", removedMarkerPools);

// A browser request that ends in an error status without a page of its own (an unknown address, a missing post) gets the
// error page inside the normal layout, so the menu still works. Files, scripts and JSON calls keep their plain status.
app.UseWhen(
    context => (HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method))
        && context.Request.Headers.Accept.ToString().Contains("text/html", StringComparison.OrdinalIgnoreCase)
        && !context.Request.Path.StartsWithSegments("/media") && !context.Request.Path.StartsWithSegments("/dist"),
    branch => branch.UseStatusCodePagesWithReExecute("/error", "?code={0}"));

// The rubbish bin lives in <media>/.bin; nothing below a dot folder is served.
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/media") && context.Request.Path.Value!.Contains("/."))
        context.Response.StatusCode = StatusCodes.Status404NotFound;
    else
        await next();
});
app.UseStaticFiles();

// Downloaded files, read-only. The folder exists once the compose volume is mounted.
if (Directory.Exists(WebPaths.MediaDir))
{
    // .swf is not in ASP.NET's list of known types, and unknown types are not served.
    var mediaTypes = new Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider();
    mediaTypes.Mappings[".swf"] = "application/x-shockwave-flash";
    app.UseStaticFiles(new StaticFileOptions { FileProvider = new PhysicalFileProvider(WebPaths.MediaDir), RequestPath = "/media", ContentTypeProvider = mediaTypes });
}
// Routing starts here, after the error-page middleware above: re-executing a request for the error page must route it again.
app.UseRouting();
app.MapRazorPages();

// Favorites. PUT/DELETE with no body: a cross-site page cannot send them without a CORS preflight.
app.MapPut("/favorites/{id:long}", (long id, DownloadedPostsRepository posts, FavoritesRepository favorites) =>
{
    if (!posts.Exists(id)) return Results.NotFound();
    favorites.Add(id);
    return Results.Ok(new { favorited = true });
});
app.MapDelete("/favorites/{id:long}", (long id, FavoritesRepository favorites) =>
{
    favorites.Remove(id);
    return Results.Ok(new { favorited = false });
});

// Delete mode: moves a local post to the rubbish bin (the worker moves the files). Off unless enabled in Settings.
app.MapDelete("/posts/{id:long}", async (long id, SettingsRepository settings, SpankBank.Data.RubbishBin.BinStore bin, FavoritesRepository favorites, WorkerClient worker) =>
{
    if (settings.Get(WorkerSettingsKeys.DeleteModeEnabled) != "true")
        return Results.Json(new { message = "Delete mode is off. Turn it on in Settings." }, statusCode: 403);
    if (!bin.RequestDelete(id)) return Results.NotFound();
    favorites.Remove(id); // a binned post is no longer a favorite; restoring does not bring the star back
    await worker.NotifyAsync("bin");
    return Results.Ok(new { deleted = true });
});

// Thumbnails of binned posts, read from <media>/.bin (the media folder's static route never serves dot folders).
app.MapGet("/bin/preview/{id:long}", (long id, SpankBank.Data.RubbishBin.BinStore bin) =>
{
    var post = bin.Get(id);
    if (post is null || post.Md5.Length != 32) return Results.NotFound();
    foreach (var root in new[] { Path.Combine(WebPaths.MediaDir, SpankBank.Data.RubbishBin.BinStore.BinFolder), WebPaths.MediaDir })
    {
        var preview = MediaPaths.Preview(root, post.Md5);
        if (File.Exists(preview)) return Results.File(preview, "image/jpeg");
    }
    return Results.NotFound();
});

// Favorite folders. JSON in, JSON out; a broken rule answers 422 with a message for the user.
IResult Folder(Func<object> action)
{
    try { return Results.Ok(action()); }
    catch (FavoriteFolderException e) { return Results.Json(new { message = e.Message }, statusCode: 422); }
}
app.MapPost("/favorite_folders", (FolderRequest body, FavoriteFolders folders) => Folder(() => folders.Create(body.Name, body.ParentId)));
app.MapMethods("/favorite_folders/{id:long}", ["PATCH"], (long id, FolderRequest body, FavoriteFolders folders) => Folder(() => folders.Rename(id, body.Name)));
app.MapPost("/favorite_folders/{id:long}/move", (long id, FolderRequest body, FavoriteFolders folders) => Folder(() => folders.Move(id, body.ParentId)));
app.MapDelete("/favorite_folders/{id:long}", (long id, FavoriteFolders folders) => Folder(() => new { parent_id = folders.Delete(id) }));
app.MapPost("/favorites/{id:long}/move", (long id, FolderRequest body, FavoriteFolders folders) => Folder(() => { folders.MovePost(id, body.FolderId); return new { id, folder_id = body.FolderId }; }));

// e621ng-compatible tag autocomplete (js/components/autocomplete/providers/TagProvider.ts).
app.MapGet("/tags/autocomplete.json", (HttpContext http, TagsRepository tags, LocalTagCounts localCounts) =>
{
    var term = http.Request.Query["search[name_matches]"].ToString();
    int? category = int.TryParse(http.Request.Query["search[category]"], out var c) ? c : null;
    var found = tags.Autocomplete(term, category);
    var local = localCounts.Get(found.Select(t => t.Name));
    return Results.Json(found.Select(t => new
    {
        id = t.Id,
        name = t.Name,
        post_count = t.PostCount,
        local_count = local.GetValueOrDefault(t.Name),
        category = t.Category,
        antecedent_name = t.AntecedentName,
    }));
});

// Used to enable the "add character" button only for a tag that exists.
app.MapGet("/tags/exists.json", (string name, int? category, WatchesRepository watches) =>
    Results.Json(new { exists = category == 4 && watches.IsCharacterTag(WatchesRepository.Normalize(name)) }));

// Live progress for the Status page, relayed from the worker.
app.MapGet("/status/progress", async (WorkerClient worker) =>
    await worker.GetProgressJsonAsync() is { } json
        ? Results.Content(json, "application/json")
        : Results.Json(new { online = false }));

// Count the local tags now, in the background, so the first page that shows them does not wait.
_ = Task.Run(() => app.Services.GetRequiredService<LocalTagCounts>().Warm());

app.Run();

/// <summary>The JSON body of the folder endpoints; each endpoint reads the fields it needs.</summary>
internal sealed record FolderRequest(
    [property: System.Text.Json.Serialization.JsonPropertyName("name")] string? Name = null,
    [property: System.Text.Json.Serialization.JsonPropertyName("parent_id")] long? ParentId = null,
    [property: System.Text.Json.Serialization.JsonPropertyName("folder_id")] long? FolderId = null);
