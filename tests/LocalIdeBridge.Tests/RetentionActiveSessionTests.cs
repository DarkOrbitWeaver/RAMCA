using Xunit;

namespace LocalIdeBridge.Tests;

public sealed class RetentionActiveSessionTests
{
    [Fact]
    public async Task RetentionNeverPrunesSessionOwningActiveJob()
    {
        await using var sb = await TestSandbox.CreateAsync(
            cfg => cfg.Retention.MaxSessionCount = 5, startListener: false);
        var db = sb.Host.Database;
        var original = db.ActiveSessionId;
        const string job = "job_retention_active";
        db.CreateJob(job, original, sb.WorkspaceDir, "read_file", "{}", isIdempotent: true);
        db.UpdateJobStatus(job, "running");
        for (var i = 0; i < 8; i++)
            db.CreateAndActivateSession(sb.WorkspaceDir);

        Assert.NotNull(db.GetSession(original));
        Assert.Equal("running", db.GetJob(job)!.Status);
        db.UpdateJobStatus(job, "succeeded");
        db.PruneOldSessions();
        Assert.Null(db.GetSession(original));
        Assert.Null(db.GetJob(job));
    }

    [Fact]
    public async Task RetentionNeverPrunesSessionOwningReservedRunningOperation()
    {
        await using var sb = await TestSandbox.CreateAsync(
            cfg => cfg.Retention.MaxSessionCount = 5, startListener: false);
        var db = sb.Host.Database;
        var original = db.ActiveSessionId;
        var reservation = db.TryReserveOperation("retained_active_123", original,
            sb.WorkspaceDir, "read_file", new string('a', 64));
        Assert.True(reservation.Created);
        for (var i = 0; i < 8; i++)
            db.CreateAndActivateSession(sb.WorkspaceDir);
        Assert.NotNull(db.GetSession(original));
        Assert.Equal("running", db.GetOperation("retained_active_123")!.Status);
    }
}
