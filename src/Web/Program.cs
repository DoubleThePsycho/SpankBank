using SpankBank.Data;
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
builder.Services.AddSingleton(new WatchesRepository(paths.AppDatabasePath, paths.ServerDatabasePath));
builder.Services.AddSingleton(new SettingsRepository(paths.AppDatabasePath));
builder.Services.AddSingleton(new ImportHistoryRepository(paths.AppDatabasePath));

var app = builder.Build();

app.UseStaticFiles();
app.MapRazorPages();

app.Run();
