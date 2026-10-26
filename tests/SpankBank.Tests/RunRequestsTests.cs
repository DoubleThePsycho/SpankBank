using SpankBank.Data;
using SpankBank.Data.Repositories;

namespace SpankBank.Tests;

public class RunRequestsTests : IDisposable
{
    private readonly TempDatabases _db = new();
    private readonly SettingsRepository _settings;
    private readonly RunRequests _requests;

    public RunRequestsTests()
    {
        _settings = new SettingsRepository(_db.Paths.AppDatabasePath);
        _requests = new RunRequests(_settings);
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public void Nothing_is_pending_at_first()
    {
        Assert.False(_requests.IsPending);
        Assert.False(_requests.TryBegin());
    }

    [Fact]
    public void A_request_is_claimed_and_cleared_on_success()
    {
        _settings.RequestExportRun();
        Assert.True(_requests.TryBegin());
        _requests.Succeeded();
        Assert.False(_requests.IsPending);
    }

    [Fact]
    public void A_request_made_during_a_run_survives_its_success()
    {
        _settings.RequestExportRun();
        Assert.True(_requests.TryBegin());
        _settings.RequestExportRun(); // clicked again while running
        _requests.Succeeded();
        Assert.True(_requests.IsPending);
    }

    [Fact]
    public void A_failure_keeps_the_request_pending_until_attempts_run_out()
    {
        _settings.RequestExportRun();
        for (var attempt = 1; attempt < RunRequests.MaxAttempts; attempt++)
        {
            Assert.True(_requests.TryBegin());
            Assert.False(_requests.Failed());
            Assert.True(_requests.IsPending);
        }

        Assert.True(_requests.TryBegin());
        Assert.True(_requests.Failed()); // exhausted
        Assert.False(_requests.IsPending);
    }

    [Fact]
    public void An_exhausted_request_stays_dropped_across_restarts()
    {
        _settings.RequestExportRun();
        for (var i = 0; i < RunRequests.MaxAttempts; i++)
        {
            _requests.TryBegin();
            _requests.Failed();
        }

        var afterRestart = new RunRequests(new SettingsRepository(_db.Paths.AppDatabasePath));
        Assert.False(afterRestart.IsPending);
        Assert.False(afterRestart.TryBegin());
    }

    [Fact]
    public void A_new_request_resets_the_failure_count()
    {
        _settings.RequestExportRun();
        for (var i = 0; i < RunRequests.MaxAttempts; i++)
        {
            _requests.TryBegin();
            _requests.Failed();
        }

        _settings.RequestExportRun();
        Assert.Equal(0, _requests.Failures);
        Assert.True(_requests.TryBegin());
        Assert.False(_requests.Failed()); // a full set of attempts again
    }

    [Fact]
    public void A_run_interrupted_by_a_restart_is_picked_up_again()
    {
        _settings.RequestExportRun();
        Assert.True(_requests.TryBegin()); // worker dies here; the flag stays "running"

        var afterRestart = new RunRequests(new SettingsRepository(_db.Paths.AppDatabasePath));
        Assert.True(afterRestart.IsPending);
        Assert.True(afterRestart.TryBegin());
    }

    [Fact]
    public void Success_resets_the_failure_count()
    {
        _settings.RequestExportRun();
        _requests.TryBegin();
        _requests.Failed();
        _requests.TryBegin();
        _requests.Succeeded();
        Assert.Equal(0, _requests.Failures);
    }
}
