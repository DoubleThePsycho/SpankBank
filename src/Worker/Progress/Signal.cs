namespace SpankBank.Worker.Progress;

/// <summary>Wakes a waiting loop right away; several notifications before it wakes count as one.</summary>
public sealed class Signal
{
    private readonly SemaphoreSlim _semaphore = new(0, 1);

    public void Notify()
    {
        try { _semaphore.Release(); }
        catch (SemaphoreFullException) { /* already signalled */ }
    }

    /// <returns>True when signalled, false when the timeout passed first.</returns>
    public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
        => _semaphore.WaitAsync(timeout < TimeSpan.Zero ? TimeSpan.Zero : timeout, cancellationToken);
}
