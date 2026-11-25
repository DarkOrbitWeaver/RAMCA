using System.Text.Json.Nodes;
using LocalIdeBridge.Workflows;
using Xunit;

namespace LocalIdeBridge.Tests;

public sealed class WorkflowBindingRegressionTests
{
    private static JsonObject Bind(string step, string pointer) => new()
    {
        ["$from"] = new JsonObject { ["step"] = step, ["pointer"] = pointer }
    };

    [Fact]
    public async Task ReadShaAndPatchContent_InOneWorkflowWithoutAgentReentry()
    {
        await using var sb = await TestSandbox.CreateAsync(startListener: false);
        await File.WriteAllTextAsync(Path.Combine(sb.WorkspaceDir, "a.txt"), "original\n");
        var result = await sb.Host.Workflows.ExecuteAsync(new WorkflowDefinition
        {
            Name = "read-sha-edit",
            Steps =
            [
                new WorkflowStepSpec { Id = "read", Tool = "read_file",
                    Arguments = new JsonObject { ["path"] = "a.txt" } },
                new WorkflowStepSpec { Id = "edit", Tool = "replace_range",
                    DependsOn = ["read"],
                    Arguments = new JsonObject
                    {
                        ["path"] = "a.txt",
                        ["startLine"] = 1, ["endLine"] = 1,
                        ["newText"] = "changed",
                        ["expectedSha256"] = Bind("read", "/data/sha256")
                    }
                },
                new WorkflowStepSpec { Id = "check", Tool = "read_range",
                    DependsOn = ["edit"],
                    Arguments = new JsonObject { ["path"] = "a.txt", ["startLine"] = 1, ["endLine"] = 1 }
                }
            ]
        });
        Assert.Equal("succeeded", result.Status);
        Assert.Equal(3, result.SucceededSteps);
        Assert.Equal("changed\n", await File.ReadAllTextAsync(Path.Combine(sb.WorkspaceDir, "a.txt")));
    }

    [Fact]
    public async Task SelectedResultsReturnWithoutRepeatingReadsOrChangingMutationOutcome()
    {
        await using var sb = await TestSandbox.CreateAsync(startListener: false);
        await File.WriteAllTextAsync(Path.Combine(sb.WorkspaceDir, "a.txt"), "source");
        var result = await sb.Host.Workflows.ExecuteAsync(new WorkflowDefinition
        {
            Steps =
            [
                new WorkflowStepSpec { Id = "read", Tool = "read_file",
                    Arguments = new JsonObject { ["path"] = "a.txt" },
                    ReturnPointers = ["/data/content", "/data/sha256"] },
                new WorkflowStepSpec { Id = "write", Tool = "create_file", DependsOn = ["read"],
                    Arguments = new JsonObject { ["path"] = "b.txt", ["content"] = "written" },
                    ReturnPointers = ["/data/notThere"] }
            ]
        });
        Assert.Equal("succeeded", result.Status);
        Assert.NotNull(result.Outputs);
        Assert.Equal("source", result.Outputs!["read"]["values"]!["/data/content"]!.GetValue<string>());
        Assert.Equal(64, result.Outputs["read"]["values"]!["/data/sha256"]!.GetValue<string>().Length);
        Assert.Equal("pointer_not_found", result.Outputs["write"]["errors"]![0]!["code"]!.GetValue<string>());
        Assert.True(File.Exists(Path.Combine(sb.WorkspaceDir, "b.txt")));
    }

    [Fact]
    public async Task UndeclaredDependencyReferenceFailsBeforeMutation()
    {
        await using var sb = await TestSandbox.CreateAsync(startListener: false);
        var invalid = new WorkflowDefinition
        {
            Steps =
            [
                new WorkflowStepSpec { Id = "initial", Tool = "workspace_list" },
                new WorkflowStepSpec { Id = "bad", Tool = "create_file",
                    Arguments = new JsonObject { ["path"] = "never.txt",
                        ["content"] = Bind("initial", "/summary") } }
            ]
        };
        await Assert.ThrowsAsync<ArgumentException>(() => sb.Host.Workflows.ExecuteAsync(invalid));
        Assert.False(File.Exists(Path.Combine(sb.WorkspaceDir, "never.txt")));
    }

    [Fact]
    public async Task MissingResultPointerBlocksDownstreamWrite()
    {
        await using var sb = await TestSandbox.CreateAsync(startListener: false);
        var result = await sb.Host.Workflows.ExecuteAsync(new WorkflowDefinition
        {
            Steps =
            [
                new WorkflowStepSpec { Id = "read", Tool = "workspace_list" },
                new WorkflowStepSpec { Id = "blocked", Tool = "create_file", DependsOn = ["read"],
                    Arguments = new JsonObject { ["path"] = "blocked.txt",
                        ["content"] = Bind("read", "/data/nonexistent") }
                }
            ]
        });
        Assert.Equal("failed", result.Status);
        Assert.Equal("failed", result.Steps.Single(x => x.Id == "read").Status);
        Assert.Equal("skipped", result.Steps.Single(x => x.Id == "blocked").Status);
        Assert.False(File.Exists(Path.Combine(sb.WorkspaceDir, "blocked.txt")));
    }

    [Fact]
    public async Task RecursiveResultBindWorksWithArraysAndIndependentReads()
    {
        await using var sb = await TestSandbox.CreateAsync(startListener: false);
        await File.WriteAllTextAsync(Path.Combine(sb.WorkspaceDir, "a.txt"), "source\n");
        var result = await sb.Host.Workflows.ExecuteAsync(new WorkflowDefinition
        {
            Steps =
            [
                new WorkflowStepSpec { Id = "source", Tool = "read_file",
                    Arguments = new JsonObject { ["path"] = "a.txt" } },
                new WorkflowStepSpec { Id = "other", Tool = "repo_tree",
                    Arguments = new JsonObject { ["maxDepth"] = 1 } },
                new WorkflowStepSpec { Id = "edit", Tool = "create_file",
                    DependsOn = ["source","other"],
                    Arguments = new JsonObject
                    {
                        ["path"] = "b.txt",
                        ["content"] = Bind("source", "/data/content")
                    }
                }
            ]
        });
        Assert.Equal("succeeded", result.Status);
        Assert.Equal("source" + Environment.NewLine, await File.ReadAllTextAsync(Path.Combine(sb.WorkspaceDir, "b.txt")));
    }
}
