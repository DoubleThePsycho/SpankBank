using SpankBank.Data.Repositories;

namespace SpankBank.Data;

/// <summary>
/// Durable state of the "refresh now" request: pending, running, or given up after repeated failures.
/// It lives in the Settings table so it survives worker restarts.
/// </summary>
public sealed class RunRequests
{
    public const int MaxAttempts = 3;

    private readonly SettingsRepository _settings;

    public RunRequests(SettingsRepository settings) => _settings = settings;

    public bool IsPending => _settings.Get(WorkerSettingsKeys.RunNowRequested) is "true" or "running";

    /// <summary>Claims a pending request (a "running" one is a run a restart interrupted) and marks it running.</summary>
    public bool TryBegin()
    {
        if (_settings.Replace(WorkerSettingsKeys.RunNowRequested, "true", "running")) return true;
        return _settings.Get(WorkerSettingsKeys.RunNowRequested) == "running";
    }

    /// <summary>Done. A request made while the run was going stays pending.</summary>
    public void Succeeded()
    {
        _settings.Set(WorkerSettingsKeys.RunFailures, "0");
        _settings.Replace(WorkerSettingsKeys.RunNowRequested, "running", "false");
    }

    /// <returns>True when the attempts are used up and the request was dropped; false when it stays pending for a retry.</returns>
    public bool Failed()
    {
        var failures = int.TryParse(_settings.Get(WorkerSettingsKeys.RunFailures), out var n) ? n + 1 : 1;
        _settings.Set(WorkerSettingsKeys.RunFailures, failures.ToString());
        if (failures >= MaxAttempts)
        {
            _settings.Replace(WorkerSettingsKeys.RunNowRequested, "running", "false");
            return true;
        }
        _settings.Replace(WorkerSettingsKeys.RunNowRequested, "running", "true");
        return false;
    }

    public int Failures => int.TryParse(_settings.Get(WorkerSettingsKeys.RunFailures), out var n) ? n : 0;
}
