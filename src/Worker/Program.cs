using SpankBank.Data;
using SpankBank.Data.Downloads;
using SpankBank.Data.Matching;
using SpankBank.Data.Repositories;
using SpankBank.Worker;

var builder = Host.CreateApplicationBuilder(args);

var paths = DatabaseInitializer.GetDatabasePaths(WorkerPaths.DbDir);
DatabaseInitializer.Initialize(paths);

builder.Services.AddSingleton(paths);
builder.Services.AddSingleton(new SettingsRepository(paths.AppDatabasePath));
builder.Services.AddSingleton(sp => new ExportRefreshJob(
    paths, WorkerPaths.ImportsDir, sp.GetRequiredService<ILogger<ExportRefreshJob>>()));
builder.Services.AddSingleton(sp =>
{
    var logger = sp.GetRequiredService<ILogger<WatchMatcher>>();
    return new WatchMatcher(paths, message => logger.LogInformation("{Message}", message));
});
builder.Services.AddSingleton(new DownloadStore(paths));
builder.Services.AddHostedService<DailyScheduler>();
builder.Services.AddHostedService<DownloadService>();

await builder.Build().RunAsync();
