using System.Diagnostics;

namespace SpankBank.Worker.Progress;

public enum PhaseState { Pending, Running, Done, Skipped, Failed }

public sealed record PhaseSnapshot(string Name, PhaseState State);

public sealed record StepSnapshot(string Detail, long Done, long? Total, string Unit, double? PerSecond, double? EtaSeconds);

public sealed record RunSnapshot(string Title, IReadOnlyList<PhaseSnapshot> Phases, StepSnapshot? Step, string? Result, DateTimeOffset StartedAt, DateTimeOffset? FinishedAt);

public sealed record RecentFile(long PostId, string Md5, string Ext, string Status);

public sealed record ActiveFile(long PostId, string Name, long Done, long? Total, string Stage = "");

public sealed record DownloadSnapshot(
    bool Active, int Total, int Done, int Failed, double? FilesPerSecond, double? EtaSeconds,
    IReadOnlyList<ActiveFile> Current, IReadOnlyList<RecentFile> Recent, string? IdleReason);

public sealed record ProgressSnapshot(DateTimeOffset Now, RunSnapshot? Run, DownloadSnapshot Downloads);

/// <summary>
/// What the worker is doing right now, for the web UI's progress screen. A "run" (export refresh, matching) has a list of
/// phases and a progress step inside the current phase; downloads are one long session with per-file progress.
/// All methods are safe to call from any thread.
/// </summary>
public sealed class ProgressTracker
{
    private readonly object _lock = new();

    // ---- run
    private string? _runTitle;
    private List<(string Name, PhaseState State)> _phases = [];
    private string _stepDetail = "";
    private long _stepDone;
    private long? _stepTotal;
    private string _stepUnit = "items";
    private readonly Queue<(long Ticks, long Done)> _stepSamples = new();
    private string? _runResult;
    private DateTimeOffset _runStarted;
    private DateTimeOffset? _runFinished;
    private bool _stepVisible;

    // ---- downloads
    private bool _downloadActive;
    private int _downloadTotal, _downloadDone, _downloadFailed;
    private DateTimeOffset _downloadStarted;
    private string? _idleReason;
    private readonly Dictionary<long, ActiveFile> _current = new();
    private readonly LinkedList<RecentFile> _recent = new();
    private readonly Queue<DateTimeOffset> _completions = new();

    // ============================================================ runs

    public void BeginRun(string title, params string[] phases)
    {
        lock (_lock)
        {
            _runTitle = title;
            _phases = phases.Select(p => (p, PhaseState.Pending)).ToList();
            _runResult = null;
            _runStarted = DateTimeOffset.UtcNow;
            _runFinished = null;
            ClearStep();
        }
    }

    public void StartPhase(string name)
    {
        lock (_lock)
        {
            SetState(name, PhaseState.Running);
            ClearStep();
        }
    }

    public void EndPhase(string name, PhaseState state = PhaseState.Done)
    {
        lock (_lock)
        {
            SetState(name, state);
            ClearStep();
        }
    }

    /// <summary>Progress inside the current phase. <paramref name="total"/> may be unknown (null).</summary>
    public void Step(string detail, long done, long? total, string unit)
    {
        lock (_lock)
        {
            _stepVisible = true;
            _stepDetail = detail;
            _stepDone = done;
            _stepTotal = total;
            _stepUnit = unit;

            var now = Stopwatch.GetTimestamp();
            _stepSamples.Enqueue((now, done));
            while (_stepSamples.Count > 1 && Stopwatch.GetElapsedTime(_stepSamples.Peek().Ticks, now) > TimeSpan.FromSeconds(12))
                _stepSamples.Dequeue();
        }
    }

    public void EndRun(string result)
    {
        lock (_lock)
        {
            if (_runTitle is null) return;
            _runResult = result;
            _runFinished = DateTimeOffset.UtcNow;
            ClearStep();
        }
    }

    private void SetState(string name, PhaseState state)
    {
        var index = _phases.FindIndex(p => p.Name == name);
        if (index >= 0) _phases[index] = (name, state);
    }

    private void ClearStep()
    {
        _stepVisible = false;
        _stepSamples.Clear();
        _stepDone = 0;
        _stepTotal = null;
    }

    // ============================================================ downloads

    public void DownloadSessionStarted(int total)
    {
        lock (_lock)
        {
            _downloadActive = true;
            _downloadTotal = total;
            _downloadDone = _downloadFailed = 0;
            _downloadStarted = DateTimeOffset.UtcNow;
            _idleReason = null;
            _completions.Clear();
        }
    }

    /// <summary>More posts turned up while a session was running.</summary>
    public void DownloadSessionGrew(int more)
    {
        lock (_lock) _downloadTotal += more;
    }

    public void DownloadSessionIdle(string reason)
    {
        lock (_lock)
        {
            _downloadActive = false;
            _idleReason = reason;
            _current.Clear();
        }
    }

    public void FileStarted(long postId, string name)
    {
        lock (_lock) _current[postId] = new ActiveFile(postId, name, 0, null);
    }

    /// <summary>What a file is waiting for while it has no bytes yet ("Looking in the Wayback Machine").</summary>
    public void FileStage(long postId, string stage)
    {
        lock (_lock)
        {
            if (_current.TryGetValue(postId, out var file)) _current[postId] = file with { Stage = stage };
        }
    }

    /// <summary>Forget the "recently saved" strip, e.g. after posts were moved to the rubbish bin.</summary>
    public void ForgetRecent()
    {
        lock (_lock) _recent.Clear();
    }

    public void FileProgress(long postId, long done, long? total)
    {
        lock (_lock)
        {
            if (_current.TryGetValue(postId, out var file))
                _current[postId] = file with { Done = done, Total = total };
        }
    }

    public void FileFinished(long postId, bool saved, string md5, string ext, string status)
    {
        lock (_lock)
        {
            _current.Remove(postId);
            // A deferred post goes back in the queue: it is neither done nor failed, so the session shrinks by one.
            if (saved) _downloadDone++;
            else if (status == "deferred") _downloadTotal = Math.Max(_downloadDone + _downloadFailed, _downloadTotal - 1);
            else _downloadFailed++;

            var now = DateTimeOffset.UtcNow;
            _completions.Enqueue(now);
            while (_completions.Count > 0 && now - _completions.Peek() > TimeSpan.FromSeconds(60)) _completions.Dequeue();

            if (saved)
            {
                _recent.AddFirst(new RecentFile(postId, md5, ext, status));
                while (_recent.Count > 14) _recent.RemoveLast();
            }
        }
    }

    // ============================================================ reading

    public ProgressSnapshot Snapshot()
    {
        lock (_lock)
        {
            RunSnapshot? run = null;
            if (_runTitle is not null)
            {
                StepSnapshot? step = null;
                if (_stepVisible)
                {
                    double? perSecond = null, eta = null;
                    if (_stepSamples.Count >= 2)
                    {
                        var first = _stepSamples.Peek();
                        var last = _stepSamples.Last();
                        var seconds = Stopwatch.GetElapsedTime(first.Ticks, last.Ticks).TotalSeconds;
                        if (seconds > 0.5 && last.Done >= first.Done) perSecond = (last.Done - first.Done) / seconds;
                    }
                    if (perSecond is > 0 && _stepTotal is { } total && total > _stepDone) eta = (total - _stepDone) / perSecond;
                    step = new StepSnapshot(_stepDetail, _stepDone, _stepTotal, _stepUnit, perSecond, eta);
                }

                run = new RunSnapshot(_runTitle, _phases.Select(p => new PhaseSnapshot(p.Name, p.State)).ToList(), step, _runResult, _runStarted, _runFinished);
            }

            double? rate = null, remaining = null;
            // The rate is what finished in the last minute; samples are dropped when they age out, so a stall shows "no estimate"
            // instead of an ever slower rate and an absurd time left.
            var cutoff = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(60);
            while (_completions.Count > 0 && _completions.Peek() < cutoff) _completions.Dequeue();
            if (_downloadActive && _completions.Count >= 2)
            {
                var window = (DateTimeOffset.UtcNow - _completions.Peek()).TotalSeconds;
                if (window > 2) rate = _completions.Count / window;
                if (rate is > 0) remaining = Math.Max(0, _downloadTotal - _downloadDone - _downloadFailed) / rate;
            }

            return new ProgressSnapshot(DateTimeOffset.UtcNow, run,
                new DownloadSnapshot(_downloadActive, _downloadTotal, _downloadDone, _downloadFailed, rate, remaining,
                    _current.Values.OrderBy(f => f.PostId).ToList(), _recent.ToList(), _idleReason));
        }
    }
}
