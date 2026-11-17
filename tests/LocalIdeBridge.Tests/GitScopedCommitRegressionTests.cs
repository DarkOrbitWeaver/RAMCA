using System.Text.Json;
using System.Text.Json.Nodes;
using LocalIdeBridge.Core;
using Xunit;

namespace LocalIdeBridge.Tests;

public sealed class GitScopedCommitRegressionTests
{
    [Fact]
    public async Task ScopedGitCommitPreservesUnrelatedStagedOwnerChanges()
    {
        await using var sb = await TestSandbox.CreateAsync(startListener: false);
        var init = await sb.Host.Registry.InvokeAsync("git_branch",
            new JsonObject { ["action"] = "init" });
        Assert.True(init.Success, init.ErrorMessage);
        var owner = Path.Combine(sb.WorkspaceDir, "owner.txt");
        var agent = Path.Combine(sb.WorkspaceDir, "agent.txt");
        await File.WriteAllTextAsync(owner, "owner-old\n");
        await File.WriteAllTextAsync(agent, "agent-old\n");

        var stage = await sb.Host.Registry.InvokeAsync("git_stage",
            new JsonObject { ["all"] = true });
        Assert.True(stage.Success, stage.ErrorMessage);
        var initialCommit = await sb.Host.Registry.InvokeAsync("git_commit", new JsonObject
        {
            ["message"] = "baseline", ["allowPreviouslyStaged"] = true,
            ["authorName"] = "Fixture", ["authorEmail"] = "fixture@local.invalid"
        });
        Assert.True(initialCommit.Success, initialCommit.ErrorMessage);

        await File.WriteAllTextAsync(owner, "owner-new\n");
        await File.WriteAllTextAsync(agent, "agent-new\n");
        stage = await sb.Host.Registry.InvokeAsync("git_stage",
            new JsonObject { ["paths"] = new JsonArray("owner.txt") });
        Assert.True(stage.Success, stage.ErrorMessage);

        var refused = await sb.Host.Registry.InvokeAsync("git_commit",
            new JsonObject { ["message"] = "unscoped" });
        Assert.False(refused.Success);
        Assert.Equal("commit_scope_required", refused.ErrorCode);

        var scoped = await sb.Host.Registry.InvokeAsync("git_commit", new JsonObject
        {
            ["message"] = "agent-only",
            ["paths"] = new JsonArray("agent.txt"),
            ["authorName"] = "Fixture", ["authorEmail"] = "fixture@local.invalid"
        });
        Assert.True(scoped.Success, scoped.ErrorMessage);

        var status = await sb.Host.Registry.InvokeAsync("git_status",
            new JsonObject { ["recentCommits"] = 1 });
        Assert.True(status.Success, status.ErrorMessage);
        var parsed = JsonSerializer.SerializeToElement(status.Data, AppConfig.JsonOptions);
        Assert.Equal("main", parsed.GetProperty("branch").GetString());
        var files = parsed.GetProperty("files").EnumerateArray().ToArray();
        var stagedOwner = Assert.Single(files);
        Assert.Equal("owner.txt", stagedOwner.GetProperty("path").GetString());
        Assert.Equal("M", stagedOwner.GetProperty("indexStatus").GetString());
        Assert.True(stagedOwner.GetProperty("staged").GetBoolean());
    }
}
