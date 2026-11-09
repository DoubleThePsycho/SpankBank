using System.Diagnostics;
using Microsoft.Data.Sqlite;
using SpankBank.Data;
using SpankBank.Data.Downloads;

namespace SpankBank.Worker;

/// <summary>
/// Makes preview images for downloaded Flash files, which e621 gives none. Ruffle's headless exporter renders a frame a few
/// seconds into the movie (software Vulkan, no GPU needed); an empty frame (a preloader) is retried later in the movie.
/// The result is the usual <c>preview/xx/yy/md5.jpg</c>, so every listing picks it up. A file that cannot be rendered gets a
/// <c>.nopreview</c> marker next to the would-be preview and is not tried again (delete the marker to retry).
/// </summary>
public sealed class FlashPreviewService : BackgroundService
{
    private const string ExporterPath = "/usr/local/bin/exporter";
    private static readonly TimeSpan PassInterval = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan RenderTimeout = TimeSpan.FromSeconds(60);
    /// <summary>Frames to skip before the capture: about 3, 10 and 30 seconds of movie.</summary>
    private static readonly int[] SkipFrames = [90, 300, 900];

    private readonly DatabasePaths _paths;
    private readonly ILogger<FlashPreviewService> _logger;

    public FlashPreviewService(DatabasePaths paths, ILogger<FlashPreviewService> logger)
    {
        _paths = paths;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!File.Exists(ExporterPath))
        {
            _logger.LogInformation("The Ruffle exporter is not installed: no Flash previews will be made.");
            return;
        }

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try { await RunPassAsync(stoppingToken); }
                catch (Exception ex) when (ex is not OperationCanceledException) { _logger.LogError(ex, "Flash preview pass failed."); }
                await Task.Delay(PassInterval, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task RunPassAsync(CancellationToken cancellationToken)
    {
        var md5s = new List<string>();
        using (var connection = new SqliteConnection($"Data Source={_paths.DeviceDatabasePath};Mode=ReadOnly"))
        {
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT Md5 FROM Posts WHERE FileExt = 'swf' AND SavedAt IS NOT NULL AND length(Md5) = 32;";
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) md5s.Add(reader.GetString(0));
        }

        int made = 0, failed = 0;
        foreach (var md5 in md5s)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var swf = MediaPaths.Original(SpankBankPaths.Current.MediaDir, md5, "swf");
            var preview = MediaPaths.Preview(SpankBankPaths.Current.MediaDir, md5);
            var marker = Path.ChangeExtension(preview, ".nopreview");
            if (File.Exists(preview) || File.Exists(marker) || !File.Exists(swf)) continue;

            if (await MakeAsync(swf, preview, cancellationToken)) made++;
            else
            {
                failed++;
                Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
                await File.WriteAllTextAsync(marker, "Ruffle could not render a usable frame.", cancellationToken);
            }
        }

        if (made + failed > 0) _logger.LogInformation("Flash previews: {Made} made, {Failed} could not be rendered.", made, failed);
    }

    private async Task<bool> MakeAsync(string swf, string previewPath, CancellationToken cancellationToken)
    {
        var png = Path.Combine(Path.GetTempPath(), $"flash-{Guid.NewGuid():N}.png");
        string? best = null;
        var bestShare = 1.0;
        try
        {
            foreach (var skip in SkipFrames)
            {
                if (!await RenderAsync(swf, png, skip, cancellationToken)) continue;

                var share = FlashPreview.DominantShare(png);
                if (share < bestShare)
                {
                    bestShare = share;
                    best = png + ".best";
                    File.Copy(png, best, overwrite: true);
                }
                if (share < FlashPreview.BlankShare) break;
            }

            if (best is null || bestShare >= FlashPreview.HopelessShare) return false;
            FlashPreview.WriteJpeg(best, previewPath);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning("Flash preview for {File} failed: {Message}", Path.GetFileName(swf), ex.Message);
            return false;
        }
        finally
        {
            foreach (var file in new[] { png, png + ".best" })
                try { File.Delete(file); } catch (IOException) { }
        }
    }

    private static async Task<bool> RenderAsync(string swf, string png, int skipFrames, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(ExporterPath);
        foreach (var argument in new[] { "-g", "vulkan", "-s", "--frames", "1", "--skipframes", skipFrames.ToString(), swf, png })
            start.ArgumentList.Add(argument);
        start.Environment["MESA_SHADER_CACHE_DISABLE"] = "true";

        using var process = Process.Start(start)!;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RenderTimeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            return false;
        }
        return process.ExitCode == 0 && File.Exists(png);
    }
}
