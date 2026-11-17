using System.Text.Json.Nodes;
using LocalIdeBridge.Core;
using Xunit;

namespace LocalIdeBridge.Tests;

/// <summary>
/// Targeted regression tests for safety failures reported in the independent frontier audit.
/// All file operations run in TestSandbox's disposable workspace, never the user's projects.
/// </summary>
public sealed class AuditP0SafetyRegressionTests
{
    [Fact]
    public async Task SecondHost_CannotRecoverLiveJobsOrDeleteLiveTemp()
    {
        await using var sb = await TestSandbox.CreateAsync(startListener: false);
        var jobId = "job_keep_running_on_second_host";
        sb.Host.Database.CreateJob(jobId, sb.Host.Database.ActiveSessionId,
            sb.WorkspaceDir, "dotnet_build", "{}", isIdempotent: false);
        sb.Host.Database.UpdateJobStatus(jobId, "running");
        var keep = Path.Combine(sb.Paths.TempDir, "running-job-output.trx");
        await File.WriteAllTextAsync(keep, "live tool output");

        var error = Assert.Throws<InvalidOperationException>(() => new WorkbenchHost(sb.Paths));
        Assert.Contains("already running", error.Message);
        Assert.True(File.Exists(keep));
        Assert.Equal("running", sb.Host.Database.GetJob(jobId)!.Status);
    }

    [Fact]
    public async Task RenameOverwrite_IsRejectedWithoutDestroyingDestination()
    {
        await using var sb = await TestSandbox.CreateAsync(startListener: false);
        var source = Path.Combine(sb.WorkspaceDir, "source.txt");
        var destination = Path.Combine(sb.WorkspaceDir, "precious.txt");
        await File.WriteAllTextAsync(source, "from source");
        await File.WriteAllTextAsync(destination, "irreplaceable");

        var result = await sb.Host.Registry.InvokeAsync("rename_file", new JsonObject
        {
            ["sourcePath"] = "source.txt",
            ["destinationPath"] = "precious.txt",
            ["overwrite"] = true
        });

        Assert.False(result.Success);
        Assert.Equal("unsafe_overwrite_blocked", result.ErrorCode);
        Assert.Equal("irreplaceable", await File.ReadAllTextAsync(destination));
        Assert.Equal("from source", await File.ReadAllTextAsync(source));
    }

    [Fact]
    public async Task LegacyEncodedText_RejectsMutationInsteadOfSubstitutingCharacters()
    {
        await using var sb = await TestSandbox.CreateAsync(startListener: false);
        var fullPath = Path.Combine(sb.WorkspaceDir, "legacy.txt");
        byte[] original = [0x43, 0x61, 0x66, 0xE9, 0x0A]; // CP1252 Café
        await File.WriteAllBytesAsync(fullPath, original);

        var result = await sb.Host.Registry.InvokeAsync("replace_range", new JsonObject
        {
            ["path"] = "legacy.txt",
            ["startLine"] = 1,
            ["endLine"] = 1,
            ["newText"] = "new value"
        });
        Assert.False(result.Success);
        Assert.Equal(original, await File.ReadAllBytesAsync(fullPath));
    }

    [Fact]
    public async Task HttpsRequestedWithoutCertificate_FailsClosedInsteadOfServingHttp()
    {
        await using var sb = await TestSandbox.CreateAsync(cfg =>
        {
            cfg.Connection.Mode = "localhost";
            cfg.Connection.UseHttps = true;
            cfg.Connection.AutoGenerateSelfSignedTls = false;
            cfg.Connection.TlsCertPath = null;
        }, startListener: false);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sb.Host.StartNetworkListenerAsync());
        Assert.Contains("Refusing silent HTTP downgrade", error.Message);
    }

    [Fact]
    public async Task PausedSupervisor_StillPermitsCancellationAndRecoveryRequests()
    {
        await using var sb = await TestSandbox.CreateAsync(startListener: false);
        sb.Host.Registry.IsPaused = true;

        var cancel = await sb.Host.Registry.InvokeAsync("job_cancel", new JsonObject
        {
            ["jobId"] = "nonexistent-job"
        });
        var revert = await sb.Host.Registry.InvokeAsync("revert_edit", new JsonObject
        {
            ["editId"] = "nonexistent-edit"
        });

        Assert.NotEqual("host_paused", cancel.ErrorCode);
        Assert.NotEqual("host_paused", revert.ErrorCode);
    }
}
