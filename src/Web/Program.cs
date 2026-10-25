using SpankBank.Data;
using Microsoft.Extensions.FileProviders;
using SpankBank.Data.Downloads;
using SpankBank.Data.Import;
using SpankBank.Data.Repositories;
using SpankBank.Web.Services;

var builder = WebApplication.CreateBuilder(args);

var paths = WebPaths.Databases();
DatabaseInitializer.Initialize(paths);

builder.Services.AddRazorPages();
builder.Services.AddSingleton(paths);
builder.Services.AddSingleton(new PostsRepository(paths.ServerDatabasePath));
builder.Services.AddSingleton(new DownloadedPostsRepository(paths.DeviceDatabasePath));
builder.Services.AddSingleton(new TagsRepository(paths.ServerDatabasePath));
builder.Services.AddSingleton(new WatchesRepository(paths.AppDatabasePath, paths.ServerDatabasePath));
builder.Services.AddSingleton(new DownloadStore(paths));
builder.Services.AddSingleton(new SettingsRepository(paths.AppDatabasePath));
builder.Services.AddSingleton(new ImportHistoryRepository(paths.AppDatabasePath));

var app = builder.Build();

app.UseStaticFiles();

// Downloaded files, read-only. The folder exists once the compose volume is mounted.
if (Directory.Exists(WebPaths.MediaDir))
    app.UseStaticFiles(new StaticFileOptions { FileProvider = new PhysicalFileProvider(WebPaths.MediaDir), RequestPath = "/media" });
app.MapRazorPages();

// e621ng-compatible tag autocomplete (js/components/autocomplete/providers/TagProvider.ts).
app.MapGet("/tags/autocomplete.json", (HttpContext http, TagsRepository tags) =>
{
    var term = http.Request.Query["search[name_matches]"].ToString();
    int? category = int.TryParse(http.Request.Query["search[category]"], out var c) ? c : null;
    return Results.Json(tags.Autocomplete(term, category).Select(t => new
    {
        id = t.Id,
        name = t.Name,
        post_count = t.PostCount,
        category = t.Category,
        antecedent_name = t.AntecedentName,
    }));
});

// Used to enable the "add character" button only for a tag that exists.
app.MapGet("/tags/exists.json", (string name, int? category, WatchesRepository watches) =>
    Results.Json(new { exists = category == 4 && watches.IsCharacterTag(WatchesRepository.Normalize(name)) }));

app.Run();
