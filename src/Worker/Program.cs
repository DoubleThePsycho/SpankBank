using SpankBank.Data;
using SpankBank.Data.Downloads;
using SpankBank.Data.Matching;
using SpankBank.Data.Repositories;
using SpankBank.Worker;
using SpankBank.Worker.Progress;

SpankBank.Core.SiteConfig.ConfigureFromEnvironment(); // fails at start without SITE_HOST

var builder = Host.CreateApplicationBuilder(args);

var paths = SpankBankPaths.Current.Databases();
DatabaseInitializer.Initialize(paths);
SpankBank.Data.Import.PoolPostsIndex.EnsureFilled(paths.ServerDatabasePath);
new LocalTagSync(paths).Run(); // local posts' tags follow the catalog's (a no-op when nothing differs)

builder.Services.AddSingleton(paths);
builder.Services.AddSingleton(new SettingsRepository(paths.AppDatabasePath));
builder.Services.AddSingleton<ProgressTracker>();
builder.Services.AddSingleton<WorkerSignals>();
builder.Services.AddSingleton(sp => new ExportRefreshJob(
    paths, SpankBankPaths.Current.ImportsDir, sp.GetRequiredService<ProgressTracker>(), sp.GetRequiredService<ILogger<ExportRefreshJob>>()));
builder.Services.AddSingleton(sp =>
{
    var logger = sp.GetRequiredService<ILogger<WatchMatcher>>();
    return new WatchMatcher(paths, message => logger.LogInformation("{Message}", message));
});
builder.Services.AddSingleton(new DownloadStore(paths));
builder.Services.AddSingleton(new SpankBank.Data.Blacklist.BlockedPostsRefresher(paths));
builder.Services.AddHostedService<DailyScheduler>();
builder.Services.AddSingleton(new SpankBank.Data.RubbishBin.BinStore(paths));
builder.Services.AddHostedService<DownloadService>();
builder.Services.AddSingleton(sp => new SpankBank.Data.Downloads.LocalImportStore(paths, sp.GetRequiredService<DownloadStore>()));
builder.Services.AddHostedService<BinService>();
builder.Services.AddHostedService<LocalImportService>();
builder.Services.AddHostedService<FlashPreviewService>();
builder.Services.AddHostedService<CommandServer>();

await builder.Build().RunAsync();
