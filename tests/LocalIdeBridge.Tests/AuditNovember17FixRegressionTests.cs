using System.Net;
using System.Text.Json.Nodes;
using LocalIdeBridge.Core;
using LocalIdeBridge.Execution;
using LocalIdeBridge.Tools;
using LocalIdeBridge.Workflows;
using Xunit;

namespace LocalIdeBridge.Tests;

/// <summary>Isolated non-destructive regressions for audited November 17 behaviors.</summary>
public sealed class AuditNovember17FixRegressionTests
{
    [Fact]
    public async Task CompilerDiagnosticsBuildMode_IsNotAdvertisedReadOnly()
    {
        await using var sb = await TestSandbox.CreateAsync(startListener: false);
        Assert.False(sb.Host.Registry.Find("compiler_diagnostics")!.IsReadOnly);
    }

    [Fact]
    public async Task RoslynDiagnosticsRemainAvailableWhenPaused_ButBuildModeIsDenied()
    {
        await using var sb = await TestSandbox.CreateAsync(startListener: false);
        await File.WriteAllTextAsync(Path.Combine(sb.WorkspaceDir, "hello.cs"),
            "public class Hello { }\n");
        sb.Host.Registry.IsPaused = true;
        try
        {
            var roslyn = await sb.Host.Registry.InvokeAsync("compiler_diagnostics",
                new JsonObject { ["mode"] = "roslyn" });
            Assert.True(roslyn.Success, roslyn.ErrorMessage);
            var build = await sb.Host.Registry.InvokeAsync("compiler_diagnostics",
                new JsonObject { ["mode"] = "build" });
            Assert.False(build.Success);
            Assert.Equal("host_paused", build.ErrorCode);
        }
        finally { sb.Host.Registry.IsPaused = false; }
    }

    [Fact]
    public async Task BuildDiagnosticsWaitForWorkspaceWriteLock()
    {
        await using var sb = await TestSandbox.CreateAsync(startListener: false);
        await File.WriteAllTextAsync(Path.Combine(sb.WorkspaceDir, "Dummy.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        await File.WriteAllTextAsync(Path.Combine(sb.WorkspaceDir, "Dummy.cs"),
            "public sealed class Dummy { }\n");
        var mutex = await sb.Host.Guard.AcquireWriteLockAsync(sb.WorkspaceDir);
        Task<ToolResult>? inFlight = null;
        try
        {
            inFlight = sb.Host.Registry.InvokeAsync("compiler_diagnostics",
                new JsonObject { ["mode"] = "build", ["target"] = "Dummy.csproj" });
            await Task.Delay(150);
            Assert.False(inFlight.IsCompleted, "Build-mode diagnostics must not bypass active workspace write lock.");
        }
        finally { mutex.Dispose(); }
        var result = await inFlight!.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(result.Success, result.ErrorMessage);
    }

    [Fact]
    public async Task DeletedAccessKeyFile_RevokesPreviouslyCachedCredential()
    {
        await using var sb = await TestSandbox.CreateAsync(startListener: false);
        var key = sb.Host.Credentials.GetSecretForExplicitCopy();
        Assert.True(sb.Host.Credentials.Verify(key));
        File.Delete(sb.Paths.SecretKeyFile);
        await Task.Delay(350);
        Assert.False(sb.Host.Credentials.Verify(key));
    }

    [Fact]
    public async Task MalformedRepositoryPolicy_FailsClosedForFileReads()
    {
        await using var sb = await TestSandbox.CreateAsync(startListener: false);
        await File.WriteAllTextAsync(Path.Combine(sb.WorkspaceDir, "important.txt"), "keep");
        await File.WriteAllTextAsync(Path.Combine(sb.WorkspaceDir, ".localidebridge.json"), "{malformed");
        var result = await sb.Host.Registry.InvokeAsync("read_file",
            new JsonObject { ["path"] = "important.txt" });
        Assert.False(result.Success);
        Assert.Equal("workspace_violation", result.ErrorCode);
    }

    [Fact]
    public async Task AuthenticatedRequests_AreSubjectToConfiguredRequestLimit()
    {
        await using var sb = await TestSandbox.CreateAsync(cfg => cfg.Security.RequestsPerMinute = 10);
        using var client = sb.CreateClient();
        for (var i = 0; i < 10; i++)
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("api/v1/info")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.GetAsync("api/v1/info")).StatusCode);
    }

    [Fact]
    public async Task DeniedGitPush_IsRejectedThroughShellCommand()
    {
        await using var sb = await TestSandbox.CreateAsync(startListener: false);
        var result = await sb.Host.Registry.InvokeAsync("run_command",
            new JsonObject { ["command"] = "git push origin main", ["useShell"] = true });
        Assert.False(result.Success);
        Assert.Equal("workspace_violation", result.ErrorCode);
    }

    [Fact]
    public async Task TimedOutJob_HasCompletionTimestamp()
    {
        await using var sb = await TestSandbox.CreateAsync(startListener: false);
        const string id = "job_timeout_report_regression";
        sb.Host.Database.CreateJob(id, sb.Host.Database.ActiveSessionId,
            sb.WorkspaceDir, "read_file", "{}", isIdempotent: true);
        sb.Host.Database.UpdateJobStatus(id, "timed_out", errorMessage: "deadline");
        var job = sb.Host.Database.GetJob(id)!;
        Assert.Equal("timed_out", job.Status);
        Assert.NotNull(job.CompletedAtUtc);
    }

    [Fact]
    public async Task MalformedTrxMissingCounters_IsRejected()
    {
        await using var sb = await TestSandbox.CreateAsync(startListener: false);
        var path = Path.Combine(sb.Paths.TempDir, "incomplete.trx");
        await File.WriteAllTextAsync(path,
            "<TestRun><ResultSummary><Counters passed=\"0\" /></ResultSummary></TestRun>");
        Assert.Throws<InvalidDataException>(() => ProcessRunner.ParseTrxFile(path));
    }

    [Fact]
    public async Task CommandLogs_BelongToOriginalInvocationSessionAfterSwitch()
    {
        await using var sb = await TestSandbox.CreateAsync(startListener: false);
        var original = sb.Host.Database.ActiveSessionId;
        sb.Host.Database.CreateAndActivateSession(sb.WorkspaceDir);
        var result = await sb.Host.Registry.InvokeAsync("run_command", new JsonObject
        {
            ["command"] = "dotnet", ["arguments"] = new JsonArray("--version")
        }, new ToolExecutionContext(original, WorkspaceRoot: sb.WorkspaceDir));
        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotNull(result.ExecutionId);
        Assert.Equal(original, sb.Host.Database.GetLogMeta(result.ExecutionId!)!.Value.SessionId);
    }

    [Fact]
    public async Task GlobalFailureHandler_RunsAfterEarlierIndependentStepFails()
    {
        await using var sb = await TestSandbox.CreateAsync(startListener: false);
        var workflow = new WorkflowDefinition
        {
            Name = "global failure handler regression",
            Steps =
            [
                new WorkflowStepSpec
                {
                    Id = "failing", Tool = "read_file",
                    Arguments = new JsonObject { ["path"] = "does-not-exist.txt" }
                },
                new WorkflowStepSpec
                {
                    Id = "on_failure", Tool = "workspace_list", RunIf = "on_failure"
                }
            ]
        };
        var result = await sb.Host.Workflows.ExecuteAsync(workflow);
        Assert.Equal("failed", result.Status);
        Assert.Equal("failed", result.Steps.Single(s => s.Id == "failing").Status);
        Assert.Equal("succeeded", result.Steps.Single(s => s.Id == "on_failure").Status);
    }

    [Fact]
    public async Task PendingPatchRecovery_PrecedesOldSessionSnapshotPruning()
    {
        await using var sb = await TestSandbox.CreateAsync(
            cfg => cfg.Retention.MaxSessionCount = 5, startListener: false);
        var firstSession = sb.Host.Database.ActiveSessionId;
        const string before = "precious-original\\n";
        const string after = "interrupted-agent-edit\\n";
        var full = Path.Combine(sb.WorkspaceDir, "recovery_order.txt");
        await File.WriteAllTextAsync(full, after);

        var backup = Path.Combine(sb.Paths.GetSessionEditsDir(firstSession), "edit_recovery_order.bak");
        await File.WriteAllTextAsync(backup, before);
        var shaBefore = Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(before)));
        var shaAfter = Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(after)));
        var pendingDir = Path.Combine(sb.Paths.DataDir, "pending-patches");
        Directory.CreateDirectory(pendingDir);
        var manifest = Path.Combine(pendingDir, "patch_recovery_order.json");
        File.WriteAllText(manifest, System.Text.Json.JsonSerializer.Serialize(new
        {
            id = "patch_recovery_order", workspace = sb.WorkspaceDir,
            sessionId = firstSession, committed = false,
            items = new[] { new {
                relativePath = "recovery_order.txt", editId = "edit_recovery_order",
                backupPath = backup, beforeHash = shaBefore, afterHash = shaAfter
            } }
        }, AppConfig.JsonOptions));

        // Four more sessions fill the retention allowance; restart creates the
        // sixth. The first session has no edit-journal record yet: only a
        // recovery manifest and its backup, which must be used before pruning.
        for (var i = 0; i < 4; i++)
            sb.Host.Database.CreateAndActivateSession(sb.WorkspaceDir);
        await sb.Host.DisposeAsync();
        await using var recovered = new WorkbenchHost(sb.Paths, sb.Config);
        Assert.Equal(before, await File.ReadAllTextAsync(full));
        Assert.False(File.Exists(manifest));
    }

    [Fact]
    public async Task McpCancellationNotification_StopsRunningHttpToolCall()
    {
        await using var sb = await TestSandbox.CreateAsync();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        sb.Host.Registry.Register(new ToolDefinition(
            Name: "test_wait_for_cancel", Category: "test",
            Description: "Test-only cancellable wait.", IsReadOnly: true,
            IsIdempotent: true, SideEffects: "None.", Parameters: [],
            Handler: async (_, _, ct) =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return ToolResult.Ok("Unexpected completion");
            }));

        using var client = sb.CreateClient();
        var call = client.PostAsync("mcp", TestSandbox.JsonBody(new
        {
            jsonrpc = "2.0", id = 47, method = "tools/call",
            @params = new { name = "test_wait_for_cancel", arguments = new { } }
        }));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using var cancelled = await client.PostAsync("mcp", TestSandbox.JsonBody(new
        {
            jsonrpc = "2.0", method = "notifications/cancelled",
            @params = new { requestId = 47, reason = "regression test" }
        }));
        Assert.Equal(HttpStatusCode.Accepted, cancelled.StatusCode);
        using var response = await call.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("cancelled", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("isError", body);
    }
}
