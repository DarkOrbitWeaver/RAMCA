using System.Text.Json;
using System.Text.Json.Nodes;
using LocalIdeBridge.Core;

namespace LocalIdeBridge.Tools;

/// <summary>Bounded on-demand schema discovery and read-only dispatch through the authoritative registry.</summary>
public static class DiscoveryTools
{
    internal static bool IsCompactTool(string name) => name is
        "workspace_status" or "workspace_select" or "project_context" or
        "text_search" or "filename_search" or "read_context" or "apply_patch" or
        "replace_range" or "job_start" or "job_events" or "job_status" or
        "execute_once" or "operation_status" or "git_status" or "git_diff" or
        "workflow_run" or "tool_discover" or "tool_read";

    public static IEnumerable<ToolDefinition> Create(ToolRegistry registry)
    {
        yield return new ToolDefinition(
            Name: "tool_discover", Category: "discovery",
            Description: "Find tools by exact name, keyword or category; get an exact schema when needed. Omit query for a compact paged directory.",
            IsReadOnly: true, IsIdempotent: true, SideEffects: "None.",
            Parameters:
            [
                new("query", "string", "Name, category or description keyword."),
                new("offset", "integer", "Directory offset.", Default: 0),
                new("limit", "integer", "1..20 matches.", Default: 8),
                new("schemas", "boolean", "Include input schemas; exact-name lookup always does.", Default: false)
            ],
            Handler: (args, _, ct) =>
            {
                var query = (args.OptionalString("query") ?? "").Trim();
                if (query.Length > 128) throw new ArgumentException("Query exceeds 128 characters.");
                var offset = Math.Clamp(args.OptionalInt("offset", 0), 0, registry.All.Count);
                var limit = Math.Clamp(args.OptionalInt("limit", 8), 1, 20);
                var exact = query.Length == 0 ? null : registry.Find(query);
                var matches = exact is not null ? new ToolDefinition[] { exact } : registry.All.Where(t =>
                    query.Length == 0 || t.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    t.Category.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    t.Description.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
                var includeSchemas = exact is not null || args.OptionalBool("schemas", false);
                var items = new JsonArray();
                foreach (var tool in matches.Skip(offset).Take(limit))
                {
                    ct.ThrowIfCancellationRequested();
                    var item = new JsonObject
                    {
                        ["name"] = tool.Name, ["category"] = tool.Category,
                        ["description"] = tool.Description, ["readOnly"] = tool.IsReadOnly,
                        ["idempotent"] = tool.IsIdempotent
                    };
                    if (includeSchemas) item["inputSchema"] = tool.BuildJsonSchema();
                    items.Add(item);
                }
                var data = new JsonObject { ["tools"] = items, ["total"] = matches.Length };
                // Keep exact schema reads complete; for broad discovery reduce the page
                // rather than truncate a schema into invalid or misleading JSON.
                while (true)
                {
                    var next = offset + items.Count;
                    data["nextOffset"] = next < matches.Length ? JsonValue.Create(next) : null;
                    if (JsonSerializer.SerializeToUtf8Bytes(data, AppConfig.JsonOptions).Length <= 16_384) break;
                    if (items.Count <= 1)
                        return Task.FromResult(ToolResult.Fail("discovery_budget_exceeded", "Request a smaller discovery page."));
                    items.RemoveAt(items.Count - 1);
                }
                return Task.FromResult(ToolResult.Ok($"Found {matches.Length} tool(s).", data));
            });

        yield return new ToolDefinition(
            Name: "tool_performance", Category: "discovery",
            Description: "Bounded current-host tool timing metrics: handler and durable audit latency, counts and approximate p95 buckets.",
            IsReadOnly: true, IsIdempotent: true, SideEffects: "None.",
            Parameters: [new("tool", "string", "Optional exact tool name; omit for all registered tools.")],
            Handler: (args, _, _) =>
            {
                var name = args.OptionalString("tool");
                if (name is not null && registry.Find(name) is null)
                    return Task.FromResult(ToolResult.Fail("unknown_tool", $"Unknown tool '{name}'."));
                return Task.FromResult(ToolResult.Ok("Current-host tool timings (excludes network and model latency).",
                    new { timings = registry.GetTimings(name), totalInvocations = registry.TotalInvocations }));
            });

        yield return new ToolDefinition(
            Name: "tool_read", Category: "discovery",
            Description: "Invoke a discovered read-only tool with its JSON arguments. For mutations use execute_once with a stable operationId.",
            IsReadOnly: true, IsIdempotent: true, SideEffects: "None beyond the target read and normal audit logging.",
            Parameters:
            [
                new("tool", "string", "Exact discovered read-only tool name.", Required: true),
                new("arguments", "object", "Target arguments.")
            ],
            Handler: async (args, ctx, ct) =>
            {
                var name = args.RequireString("tool");
                var target = registry.Find(name);
                if (target is null) return ToolResult.Fail("unknown_tool", $"Unknown tool '{name}'.");
                if (!target.IsReadOnly || target.Name == "tool_read")
                    return ToolResult.Fail("read_only_required", "Only non-recursive read-only tools may be invoked here.");
                if (args["arguments"] is not null and not JsonObject)
                    return ToolResult.Fail("invalid_arguments", "arguments must be an object.");
                return await registry.InvokeAsync(target.Name, args["arguments"] is JsonObject inner
                    ? (JsonObject)inner.DeepClone() : new JsonObject(), ctx, ct).ConfigureAwait(false);
            });
    }
}
