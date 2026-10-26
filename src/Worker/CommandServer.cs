using System.Net;
using System.Text.Json;
using SpankBank.Data;
using SpankBank.Data.Repositories;
using SpankBank.Worker.Progress;

namespace SpankBank.Worker;

/// <summary>
/// The worker's small internal HTTP interface, used by the web app over the compose network (not published).
///   GET  /progress               what the worker is doing right now
///   POST /commands/run-export    refresh the e621 exports now
///   POST /commands/match         re-match the watches that changed
///   POST /commands/downloads     look for posts to download
///   POST /commands/settings      settings were saved; re-read them
/// A command only wakes the loop that does the work; the work itself and its durable flags live in the Settings table,
/// so a command sent while the worker is down is still picked up when it starts.
/// </summary>
public sealed class CommandServer : BackgroundService
{
    // Fixed inside the container; the environment variable only exists for running next to other things during development.
    public static readonly int Port = int.TryParse(Environment.GetEnvironmentVariable("WORKER_COMMAND_PORT"), out var port) ? port : 8081;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly ProgressTracker _progress;
    private readonly WorkerSignals _signals;
    private readonly SettingsRepository _settings;
    private readonly ILogger<CommandServer> _logger;

    public CommandServer(ProgressTracker progress, WorkerSignals signals, SettingsRepository settings, ILogger<CommandServer> logger)
    {
        _progress = progress;
        _signals = signals;
        _settings = settings;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://+:{Port}/");
        try
        {
            listener.Start();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not start the command server on port {Port}; the web UI cannot send commands.", Port);
            return;
        }

        _logger.LogInformation("Command server listening on port {Port}.", Port);
        using var registration = stoppingToken.Register(listener.Stop);

        while (!stoppingToken.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync();
            }
            catch (Exception) when (stoppingToken.IsCancellationRequested || !listener.IsListening)
            {
                return;
            }

            try { Handle(context); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Command request failed.");
                Respond(context, 500, "error");
            }
        }
    }

    private void Handle(HttpListenerContext context)
    {
        var path = context.Request.Url?.AbsolutePath ?? "";
        var method = context.Request.HttpMethod;

        if (method == "GET" && path == "/progress")
        {
            Respond(context, 200, JsonSerializer.Serialize(_progress.Snapshot(), Json), "application/json");
            return;
        }

        if (method == "POST" && path.StartsWith("/commands/"))
        {
            switch (path["/commands/".Length..])
            {
                case "run-export":
                    _settings.RequestExportRun();
                    _signals.Scheduler.Notify();
                    break;
                case "match":
                    _settings.Set(WorkerSettingsKeys.MatchRequested, "true");
                    _signals.Scheduler.Notify();
                    break;
                case "downloads":
                    _signals.Downloads.Notify();
                    break;
                case "settings":
                    // Wake both loops: the scheduler re-reads its time, the downloader its limits.
                    _signals.Scheduler.Notify();
                    _signals.Downloads.Notify();
                    break;
                default:
                    Respond(context, 404, "unknown command");
                    return;
            }

            Respond(context, 202, "accepted");
            return;
        }

        Respond(context, 404, "not found");
    }

    private static void Respond(HttpListenerContext context, int status, string body, string contentType = "text/plain")
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(body);
        context.Response.StatusCode = status;
        context.Response.ContentType = contentType;
        context.Response.ContentLength64 = bytes.Length;
        context.Response.OutputStream.Write(bytes);
        context.Response.Close();
    }
}
