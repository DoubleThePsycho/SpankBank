using SpankBank.Data;
using SpankBank.Data.Downloads;
using SpankBank.Data.Matching;
using SpankBank.Data.Repositories;
using SpankBank.Worker;
using SpankBank.Worker.Progress;

var builder = Host.CreateApplicationBuilder(args);

var paths = DatabaseInitializer.GetDatabasePaths(WorkerPaths.DbDir);
DatabaseInitializer.Initialize(paths);
SpankBank.Data.Import.PoolPostsIndex.EnsureFilled(paths.ServerDatabasePath);

builder.Services.AddSingleton(paths);
builder.Services.AddSingleton(new SettingsRepository(paths.AppDatabasePath));
builder.Services.AddSingleton<ProgressTracker>();
builder.Services.AddSingleton<WorkerSignals>();
builder.Services.AddSingleton(sp => new ExportRefreshJob(
    paths, WorkerPaths.ImportsDir, sp.GetRequiredService<ProgressTracker>(), sp.GetRequiredService<ILogger<ExportRefreshJob>>()));
builder.Services.AddSingleton(sp =>
{
    var logger = sp.GetRequiredService<ILogger<WatchMatcher>>();
    return new WatchMatcher(paths, message => logger.LogInformation("{Message}", message));
});
builder.Services.AddSingleton(new DownloadStore(paths));
builder.Services.AddHostedService<DailyScheduler>();
builder.Services.AddHostedService<DownloadService>();
builder.Services.AddHostedService<CommandServer>();

await builder.Build().RunAsync();
