using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LocalIdeBridge.Core;
using LocalIdeBridge.Storage;

namespace LocalIdeBridge.Tools;

/// <summary>
/// Durable, fail-closed execute-once wrapper for tool calls whose responses might be lost.
/// A retry returns the previously committed result or a clear unknown outcome; it never
/// repeats a mutation. Clients MUST supply a stable operationId across retries.
/// </summary>
public static class OperationLedgerTools
{
    public static IEnumerable<ToolDefinition> Create(WorkspaceGuard guard, StateDatabase db, ToolRegistry registry)
    {
        yield return new ToolDefinition(
            Name: "execute_once",
            Category: "jobs",
            Description: "Executes a tool at most once for a client-provided operationId. Retrying the same ID returns the previous result, in-progress status, or ambiguous crash outcome WITHOUT repeating side effects. Use for risky edits, builds and Git commands after network interruptions.",
            IsReadOnly: false,
            IsIdempotent: true,
            SideEffects: "Executes the requested tool once; writes a durable local operation record before execution.",
            Parameters:
            [
                new("operationId", "string", "Stable unique identifier retained by the client across retries (not a generated new ID).", Required: true),
                new("tool", "string", "Target tool (cannot invoke execute_once recursively).", Required: true),
                new("arguments", "object", "Target tool JSON arguments (optional).")
            ],
            Handler: async (args, ctx, ct) =>
            {
                var opId = args.RequireString("operationId");
                if (opId.Length > 128 || !opId.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'))
                    return ToolResult.Fail("invalid_operation_id", "operationId must be 1..128 ASCII alphanumeric, dash, dot or underscore characters.");
                var name = args.RequireString("tool");
                if (string.Equals(name, "execute_once", StringComparison.OrdinalIgnoreCase))
                    return ToolResult.Fail("invalid_arguments", "execute_once cannot recursively invoke itself.");
                var definition = registry.Find(name);
                if (definition is null)
                    return ToolResult.Fail("unknown_tool", $"Unknown tool '{name}'.");
                var inner = args["arguments"] as JsonObject ?? new JsonObject();
                if (args.TryGetPropertyValue("arguments", out var supplied) && supplied is not null && supplied is not JsonObject)
                    return ToolResult.Fail("invalid_arguments", "arguments must be a JSON object.");

                var workspace = guard.RequireActiveWorkspace();
                var inputHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(inner.ToJsonString())));
                var (operation, created) = db.TryReserveOperation(opId, ctx.SessionId, workspace, name, inputHash);
                if (!string.Equals(operation.ToolName, name, StringComparison.Ordinal) ||
                    !string.Equals(operation.ArgumentsSha256, inputHash, StringComparison.Ordinal) ||
                    !string.Equals(operation.Workspace, workspace, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                    return ToolResult.Fail("operation_id_conflict", "This operationId already belongs to a different tool, workspace or argument set. Nothing was executed.");

                if (!created)
                {
                    if ((operation.Status is "succeeded" or "failed") && operation.ResultJson is not null)
                    {
                        var old = JsonSerializer.Deserialize<ToolResult>(operation.ResultJson, AppConfig.JsonOptions);
                        return old ?? ToolResult.Fail("stored_result_invalid", "Operation finished but stored result is unreadable; inspect before retrying.");
                    }
                    return ToolResult.Fail(operation.Status == "running" ? "operation_in_progress" : "operation_outcome_unknown",
                        $"Operation '{opId}' was previously {operation.Status}; RAMCA will not repeat its effects. Inspect the job/repository state before a new operationId is assigned.",
                        new { operationId = opId, operation.Status, operation.ToolName, operation.Workspace });
                }

                // The client request can be cancelled by network loss even though the
                // local operation is still valid. After reserving a durable operation ID,
                // do not bind the underlying execution to RequestAborted. This Task
                // remains alive while the RAMCA host process is alive; process shutdown
                // still terminates owned children and startup marks an unknown outcome.
                // For explicit cancellation, launch a cancellable job_start operation.
                // Detached HTTP calls must survive client disconnects, but explicitly
                // cancellable background jobs must honor their own cancellation token.
                var executionToken = ctx.JobId is null ? CancellationToken.None : ct;
                var result = await registry.InvokeAsync(name, (JsonObject)inner.DeepClone(),
                    new ToolExecutionContext(ctx.SessionId, ctx.JobId, ctx.ReportProgress, WorkspaceRoot: workspace),
                    executionToken).ConfigureAwait(false);
                db.CompleteOperation(opId, result);
                return result;
            });

        yield return new ToolDefinition(
            Name: "operation_status",
            Category: "jobs",
            Description: "Returns the durable execution state of a previously reserved execute_once operation. 'interrupted_unknown' means do not retry blindly.",
            IsReadOnly: true,
            IsIdempotent: true,
            SideEffects: "None.",
            Parameters: [new("operationId", "string", "Operation ID supplied to execute_once.", Required: true)],
            Handler: (args, _, _) =>
            {
                var id = args.RequireString("operationId");
                var op = db.GetOperation(id);
                if (op is null) return Task.FromResult(ToolResult.Fail("not_found", $"No operation '{id}' was recorded."));
                return Task.FromResult(ToolResult.Ok($"Operation '{id}' is {op.Status}.", new
                {
                    op.OperationId, op.ToolName, op.Workspace, op.Status, op.UpdatedAtUtc,
                    result = op.ResultJson is null ? null : JsonNode.Parse(op.ResultJson)
                }));
            });
    }
}
