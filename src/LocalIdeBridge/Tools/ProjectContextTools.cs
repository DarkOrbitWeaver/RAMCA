using System.Text.Json;
using System.Text.Json.Nodes;
using LocalIdeBridge.Core;

namespace LocalIdeBridge.Tools;

/// <summary>
/// One bounded agent orientation call. Reuses authoritative read-only tools,
/// does not invent a second repository index or retain source in memory.
/// </summary>
public static class ProjectContextTools
{
    public static IEnumerable<ToolDefinition> Create(WorkspaceGuard guard, ToolRegistry registry)
    {
        yield return new ToolDefinition(
            Name: "project_context",
            Category: "code_intel",
            Description: "One-call project orientation: workspace/Git state, solution/project manifests and bounded file tree. Uses existing read-only tools; independent discovery runs concurrently. Avoid 3+ separate agent interactions.",
            IsReadOnly: true, IsIdempotent: true, SideEffects: "None.",
            Parameters:
            [
                new("maxProjects", "integer", "Maximum project/solution/manifests entries per category (1-30).", Default: 12),
                new("maxTreeEntries", "integer", "Maximum shallow repository entries (10-180).", Default: 60),
                new("maxDepth", "integer", "Tree depth (1-5).", Default: 3)
            ],
            Handler: async (args, ctx, ct) =>
            {
                var maxProjects = Math.Clamp(args.OptionalInt("maxProjects", 12), 1, 30);
                var maxTree = Math.Clamp(args.OptionalInt("maxTreeEntries", 60), 10, 180);
                var maxDepth = Math.Clamp(args.OptionalInt("maxDepth", 3), 1, 5);
                var workspace = guard.RequireActiveWorkspace();
                var pinned = new ToolExecutionContext(ctx.SessionId, ctx.JobId, WorkspaceRoot: workspace);
                // Independent read-only results can be safely gathered in parallel.
                var statusTask = registry.InvokeAsync("workspace_status", new JsonObject(), pinned, ct);
                var projectsTask = registry.InvokeAsync("project_discover", new JsonObject(), pinned, ct);
                var treeTask = registry.InvokeAsync("repo_tree", new JsonObject
                {
                    ["maxDepth"] = maxDepth, ["maxEntries"] = maxTree
                }, pinned, ct);
                await Task.WhenAll(statusTask, projectsTask, treeTask).ConfigureAwait(false);
                var errors = new JsonArray();

                JsonNode? Data(ToolResult result, string section)
                {
                    if (result.Success)
                        return JsonSerializer.SerializeToNode(result.Data, AppConfig.JsonOptions);
                    errors.Add(new JsonObject { ["section"] = section, ["code"] = result.ErrorCode,
                        ["message"] = result.ErrorMessage });
                    return null;
                }

                var status = Data(await statusTask, "workspace");
                var projectInfo = Data(await projectsTask, "projects");
                var tree = Data(await treeTask, "tree");
                static JsonArray Take(JsonNode? root, string prop, int limit)
                {
                    if (root?[prop] is not JsonArray arr) return new JsonArray();
                    return new JsonArray(arr.Take(limit).Select(node => node?.DeepClone()).ToArray());
                }
                var payload = new JsonObject
                {
                    ["workspace"] = workspace,
                    ["gitBranch"] = status?["gitBranch"]?.DeepClone(),
                    ["dirtyFileCount"] = status?["modifiedOrUntrackedCount"]?.DeepClone(),
                    ["isGitRepository"] = status?["isGitRepository"]?.DeepClone(),
                    ["sessionId"] = ctx.SessionId,
                    ["supervisorNote"] = status?["supervisorSteeringNote"]?.DeepClone(),
                    ["solutions"] = Take(projectInfo, "solutions", maxProjects),
                    ["projects"] = Take(projectInfo, "projects", maxProjects),
                    ["manifests"] = Take(projectInfo, "manifests", maxProjects),
                    ["tree"] = Take(tree, "entries", maxTree),
                    ["treeTruncated"] = tree?["truncated"]?.DeepClone(),
                    ["errors"] = errors
                };
                var serialized = JsonSerializer.SerializeToUtf8Bytes(payload, AppConfig.JsonOptions);
                if (serialized.Length > 24_000)
                {
                    // Large project metadata is not silently claimed complete.
                    payload["truncated"] = true;
                    foreach (var field in new[] { "tree", "projects", "solutions", "manifests" })
                        if (payload[field] is JsonArray list)
                            while (list.Count > 0 &&
                                JsonSerializer.SerializeToUtf8Bytes(payload, AppConfig.JsonOptions).Length > 24_000)
                                list.RemoveAt(list.Count - 1);
                    if (JsonSerializer.SerializeToUtf8Bytes(payload, AppConfig.JsonOptions).Length > 24_000)
                        return ToolResult.Fail("context_budget_exceeded", "Project context cannot fit a 24 KiB response budget.");
                }
                return ToolResult.Ok(
                    $"Project context: {(status?["gitBranch"]?.ToString() ?? "unknown")} · " +
                    $"{payload["dirtyFileCount"]?.ToString() ?? "unknown"} changed · " +
                    $"{(payload["projects"] as JsonArray)?.Count ?? 0} projects" +
                    (errors.Count > 0 ? $" ({errors.Count} partial errors)." : "."),
                    payload);
            });
    }
}
