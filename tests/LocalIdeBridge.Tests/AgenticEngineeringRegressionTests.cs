using System.Text.Json;
using System.Text.Json.Nodes;
using LocalIdeBridge.Core;
using LocalIdeBridge.Tools;
using LocalIdeBridge.Workflows;
using Xunit;

namespace LocalIdeBridge.Tests;

public sealed class AgenticEngineeringRegressionTests
{
    [Theory]
    [InlineData(2, "a\nb\nINSERT\nc\n")]
    [InlineData(0, "INSERT\na\nb\nc\n")]
    [InlineData(3, "a\nb\nc\nINSERT\n")]
    public async Task ZeroContextPatchesInsertAfterDeclaredOldLine(int oldStart, string expected)
    {
        await using var sb = await TestSandbox.CreateAsync(startListener: false);
        await File.WriteAllTextAsync(Path.Combine(sb.WorkspaceDir, "a.txt"), "a\nb\nc\n");
        var newStart = oldStart + 1;
        var patch = $"--- a/a.txt\n+++ b/a.txt\n@@ -{oldStart},0 +{newStart},1 @@\n+INSERT\n";
        var r = await sb.Host.Registry.InvokeAsync("apply_patch", new JsonObject { ["patch"] = patch });
        Assert.True(r.Success, r.ErrorMessage);
        Assert.Equal(expected.Replace("\n", Environment.NewLine), await File.ReadAllTextAsync(Path.Combine(sb.WorkspaceDir, "a.txt")));
    }

    [Fact]
    public async Task ScalarHashInMultiFilePatchIsRejectedWithoutChanges()
    {
        await using var sb = await TestSandbox.CreateAsync(startListener: false);
        foreach (var f in new[] { "a.txt", "b.txt" })
            await File.WriteAllTextAsync(Path.Combine(sb.WorkspaceDir, f), "old\n");
        var patch = """
            --- a/a.txt
            +++ b/a.txt
            @@ -1 +1 @@
            -old
            +new
            --- a/b.txt
            +++ b/b.txt
            @@ -1 +1 @@
            -old
            +new
            """;
        var r = await sb.Host.Registry.InvokeAsync("apply_patch", new JsonObject
        {
            ["patch"] = patch, ["expectedSha256"] = new string('0', 64)
        });
        Assert.False(r.Success);
        Assert.Equal("invalid_arguments", r.ErrorCode);
        Assert.Equal("old\n", await File.ReadAllTextAsync(Path.Combine(sb.WorkspaceDir, "a.txt")));
        Assert.Equal("old\n", await File.ReadAllTextAsync(Path.Combine(sb.WorkspaceDir, "b.txt")));
    }

    [Fact]
    public async Task DefaultMainOnlyPolicyRefusesBranchAndWorktreeCreation()
    {
        await using var sb = await TestSandbox.CreateAsync(startListener: false);
        var init = await sb.Host.Registry.InvokeAsync("git_branch", new JsonObject { ["action"] = "init" });
        Assert.True(init.Success, init.ErrorMessage);
        var br = await sb.Host.Registry.InvokeAsync("git_branch", new JsonObject
        {
            ["action"] = "create", ["name"] = "feature/surprise"
        });
        Assert.False(br.Success);
        Assert.Equal("workspace_violation", br.ErrorCode);
        var wt = await sb.Host.Registry.InvokeAsync("git_worktree", new JsonObject
        {
            ["action"] = "add", ["name"] = "feature"
        });
        Assert.False(wt.Success);
        Assert.Equal("workspace_violation", wt.ErrorCode);
        var ws = await sb.Host.Registry.InvokeAsync("workspace_status", new JsonObject());
        Assert.True(ws.Success, ws.ErrorMessage);
        Assert.Equal("main", JsonSerializer.SerializeToElement(ws.Data, AppConfig.JsonOptions)
            .GetProperty("gitBranch").GetString());
    }

    [Fact]
    public async Task OrdinaryDotRelativePathsAreAcceptedOnWindows()
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var sb = await TestSandbox.CreateAsync(startListener: false);
        await File.WriteAllTextAsync(Path.Combine(sb.WorkspaceDir, "item.txt"), "hello\n");
        var res = await sb.Host.Registry.InvokeAsync("read_file", new JsonObject { ["path"] = "./item.txt" });
        Assert.True(res.Success, res.ErrorMessage);
    }

    [Fact]
    public async Task ExecuteOnceCanonicalizesNestedObjectKeyOrder()
    {
        await using var sb = await TestSandbox.CreateAsync(startListener: false);
        await File.WriteAllTextAsync(Path.Combine(sb.WorkspaceDir, "r.txt"), "data\n");
        var id = "canonical-json-reordered-001";
        var first = await sb.Host.Registry.InvokeAsync("execute_once", new JsonObject
        {
            ["operationId"] = id,
            ["tool"] = "read_file",
            ["arguments"] = new JsonObject { ["path"] = "r.txt", ["maxChars"] = 1000 }
        });
        Assert.True(first.Success, first.ErrorMessage);
        var second = await sb.Host.Registry.InvokeAsync("execute_once", new JsonObject
        {
            ["operationId"] = id,
            ["tool"] = "read_file",
            ["arguments"] = new JsonObject { ["maxChars"] = 1000, ["path"] = "r.txt" }
        });
        Assert.True(second.Success, second.ErrorMessage);
        Assert.Equal(first.Summary, second.Summary);
    }

    [Fact]
    public async Task GlobalFailureHandlerCanHaveDependentFollowUpSteps()
    {
        await using var sb = await TestSandbox.CreateAsync(startListener: false);
        var result = await sb.Host.Workflows.ExecuteAsync(new WorkflowDefinition
        {
            Steps =
            [
                new WorkflowStepSpec { Id = "bad", Tool = "read_file",
                    Arguments = new JsonObject { ["path"] = "missing.txt" } },
                new WorkflowStepSpec { Id = "catch", Tool = "workspace_list", RunIf = "on_failure" },
                new WorkflowStepSpec { Id = "after", Tool = "session_list",
                    DependsOn = ["catch"], RunIf = "always" }
            ]
        });
        Assert.Equal("failed", result.Status);
        Assert.Equal("succeeded", result.Steps.Single(s => s.Id == "catch").Status);
        Assert.Equal("succeeded", result.Steps.Single(s => s.Id == "after").Status);
    }

    [Fact]
    public async Task JobEventWaitUsesSignalAndReturnsTerminalWithoutPolling()
    {
        await using var sb = await TestSandbox.CreateAsync(startListener: false);
        sb.Host.Registry.Register(new ToolDefinition(
            Name: "test_slow_job_for_wait", Category: "test",
            Description: "Harmless test delay", IsReadOnly: true, IsIdempotent: true,
            SideEffects: "None", Parameters: [],
            Handler: async (_, _, ct) =>
            {
                await Task.Delay(250, ct);
                return ToolResult.Ok("completed");
            }));
        var rec = sb.Host.Jobs.StartToolJob("test_slow_job_for_wait", new JsonObject());
        var initial = sb.Host.Database.ListJobEvents(rec.Id);
        var cursor = initial.Count == 0 ? 0 : initial[^1].Id;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var response = await sb.Host.Registry.InvokeAsync("job_events", new JsonObject
        {
            ["jobId"] = rec.Id, ["afterEventId"] = cursor, ["waitMs"] = 3000
        });
        sw.Stop();
        Assert.True(response.Success, response.ErrorMessage);
        var info = JsonSerializer.SerializeToElement(response.Data, AppConfig.JsonOptions);
        Assert.True(info.GetProperty("changed").GetBoolean() ||
            info.GetProperty("terminal").GetBoolean());
        Assert.True(sw.ElapsedMilliseconds < 2900);
    }

    [Fact]
    public async Task JobQueueAdmissionRejectsOverflowBeforeCreatingJob()
    {
        await using var sb = await TestSandbox.CreateAsync(
            cfg => cfg.Resources.MaxConcurrentJobs = 1, startListener: false);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        sb.Host.Registry.Register(new ToolDefinition(
            Name: "test_blocked_job", Category: "test",
            Description: "Harmless bounded queue test", IsReadOnly: true, IsIdempotent: true,
            SideEffects: "None", Parameters: [],
            Handler: async (_, _, ct) =>
            {
                await release.Task.WaitAsync(ct);
                return ToolResult.Ok("done");
            }));
        var jobs = new List<JobRecord>();
        try
        {
            for (var i = 0; i < 8; i++)
                jobs.Add(sb.Host.Jobs.StartToolJob("test_blocked_job", new JsonObject()));
            Assert.Throws<InvalidOperationException>(
                () => sb.Host.Jobs.StartToolJob("test_blocked_job", new JsonObject()));
        }
        finally
        {
            release.TrySetResult();
            foreach (var j in jobs)
            {
                for (var n = 0; n < 50; n++)
                {
                    var status = sb.Host.Database.GetJob(j.Id)?.Status;
                    if (status is "succeeded" or "failed" or "cancelled") break;
                    await Task.Delay(25);
                }
            }
        }
    }
}
