namespace SpankBank.Worker.Progress;

/// <summary>The wake-up calls between the command server and the loops that do the work.</summary>
public sealed class WorkerSignals
{
    /// <summary>Something for the scheduler: a run-now request or changed watches.</summary>
    public Signal Scheduler { get; } = new();

    /// <summary>New posts may be waiting to download.</summary>
    public Signal Downloads { get; } = new();
}
