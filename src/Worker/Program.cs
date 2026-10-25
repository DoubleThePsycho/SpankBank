using SpankBank.Data;
using SpankBank.Data.Repositories;
using SpankBank.Worker;

var builder = Host.CreateApplicationBuilder(args);

var paths = DatabaseInitializer.GetDatabasePaths(WorkerPaths.DbDir);
DatabaseInitializer.Initialize(paths);

builder.Services.AddSingleton(paths);
builder.Services.AddSingleton(new SettingsRepository(paths.AppDatabasePath));
builder.Services.AddSingleton(sp => new ExportRefreshJob(
    paths, WorkerPaths.ImportsDir, sp.GetRequiredService<ILogger<ExportRefreshJob>>()));
builder.Services.AddHostedService<DailyScheduler>();

await builder.Build().RunAsync();
