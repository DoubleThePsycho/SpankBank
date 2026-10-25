namespace SpankBank.Web.Services;

/// <summary>Talks to the worker's internal command server (compose network only): commands and live progress.</summary>
public sealed class WorkerClient
{
    private readonly HttpClient _http;
    private readonly ILogger<WorkerClient> _logger;

    public WorkerClient(HttpClient http, ILogger<WorkerClient> logger)
    {
        _http = http;
        _logger = logger;
    }

    /// <summary>Wakes the worker for a command ("run-export", "match", "downloads"). False when it cannot be reached.</summary>
    public async Task<bool> NotifyAsync(string command)
    {
        try
        {
            using var response = await _http.PostAsync($"commands/{command}", content: null);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning("Could not reach the worker for '{Command}': {Message}", command, ex.Message);
            return false;
        }
    }

    /// <summary>The worker's progress JSON, or null when it is not reachable.</summary>
    public async Task<string?> GetProgressJsonAsync()
    {
        try
        {
            return await _http.GetStringAsync("progress");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return null;
        }
    }
}
