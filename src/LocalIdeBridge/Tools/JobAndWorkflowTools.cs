using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using LocalIdeBridge.Core;
using LocalIdeBridge.Storage;
using LocalIdeBridge.Workflows;

namespace LocalIdeBridge.Tools;

public static class JobAndWorkflowTools
{
    public static IEnumerable<ToolDefinition> Create(
        WorkspaceGuard guard,
        StateDatabase db,
        JobManager jobs,
        WorkflowEngine workflows)
    {
        yield return new ToolDefinition(
            Name: "job_start",
            Category: "jobs",
            Description: "Starts any LocalIdeBridge tool asynchronously as a background job and returns a jobId immediately for polling, event retrieval, or cancellation.",
            IsReadOnly: false,
            IsIdempotent: false,
            SideEffects: "Enqueues and executes the specified tool asynchronously; persists job record in data/state.db.",
            Parameters:
            [
                new("tool", "string", "Name of the tool to run asynchronously (e.g. 'dotnet_build', 'dotnet_test', 'workflow_run').", Required: true),
                new("arguments", "object", "Arguments object passed to the target tool."),
                new("timeoutSeconds", "integer", "Optional job timeout in seconds.")
            ],
            Handler: (args, ctx, _) =>
            {
                var toolName = args.RequireString("tool");
                JsonObject? innerArgs = null;
                if (args.TryGetPropertyValue("arguments", out var node) && node is JsonObject obj)
                    innerArgs = obj;
                int? timeout = args.ContainsKey("timeoutSeconds") ? args.OptionalInt("timeoutSeconds", 0) : null;

                JobRecord rec;
                try { rec = jobs.StartToolJob(toolName, innerArgs, timeout, ctx.SessionId, ctx.WorkspaceRoot); }
                catch (InvalidOperationException ex) when (ex.Message.StartsWith("queue_full:", StringComparison.Ordinal))
                { return Task.FromResult(ToolResult.Fail("queue_full", ex.Message)); }
                return Task.FromResult(ToolResult.Ok(
                    $"Started background job '{rec.Id}' for '{rec.ToolName}'.",
                    new
                    {
                        jobId = rec.Id,
                        sessionId = rec.SessionId,
                        tool = rec.ToolName,
                        status = rec.Status,
                        createdAtUtc = rec.CreatedAtUtc,
                        note = "Local job runs independently while LocalIdeBridge stays open. Poll with job_status/job_events or cancel with job_cancel."
                    }));
            });

        yield return new ToolDefinition(
            Name: "job_status",
            Category: "jobs",
            Description: "Inspects the current status, timestamps, result payload, error details, and log reference of an asynchronous job.",
            IsReadOnly: true,
            IsIdempotent: true,
            SideEffects: "None.",
            Parameters:
            [
                new("jobId", "string", "Job identifier returned by job_start.", Required: true),
                new("includeResult", "boolean", "Include final result payload; false for lightweight status.", Default: true)
            ],
            Handler: (args, _, _) =>
            {
                var jobId = args.RequireString("jobId");
                var includeResult = args.OptionalBool("includeResult", true);
                var job = db.GetJob(jobId, includeResult) ?? throw new KeyNotFoundException($"Job '{jobId}' not found.");
                JsonNode? parsedResult = null;
                if (includeResult && !string.IsNullOrWhiteSpace(job.ResultJson))
                {
                    try { parsedResult = JsonNode.Parse(job.ResultJson); } catch { }
                }

                return Task.FromResult(ToolResult.Ok(
                    $"Job '{job.Id}' ({job.ToolName}) is '{job.Status}'.",
                    new
                    {
                        jobId = job.Id,
                        sessionId = job.SessionId,
                        workspace = job.Workspace,
                        tool = job.ToolName,
                        status = job.Status,
                        isIdempotent = job.IsIdempotent,
                        createdAtUtc = job.CreatedAtUtc,
                        startedAtUtc = job.StartedAtUtc,
                        completedAtUtc = job.CompletedAtUtc,
                        errorMessage = job.ErrorMessage,
                        logExecId = job.LogExecId,
                        result = parsedResult
                    }));
            });

        yield return new ToolDefinition(
            Name: "job_result_slice",
            Category: "jobs",
            Description: "Retrieves a bounded character page from a large JSON job result stored durably on disk.",
            IsReadOnly: true,
            IsIdempotent: true,
            SideEffects: "None.",
            Parameters:
            [
                new("jobId", "string", "Job identifier.", Required: true),
                new("offset", "integer", "Character offset (zero-based).", Default: 0),
                new("maxChars", "integer", "Maximum returned characters (<=32000).", Default: 16000)
            ],
            Handler: (args, _, _) =>
            {
                var jobId = args.RequireString("jobId");
                var part = db.ReadJobResultSlice(jobId, args.OptionalInt("offset", 0), args.OptionalInt("maxChars", 16000));
                return Task.FromResult(ToolResult.Ok("Returned a paginated job-result slice.", new
                {
                    jobId, offset = part.Offset, hasMore = part.HasMore, nextOffset = part.HasMore ? (int?)(part.Offset + part.Content.Length) : null, content = part.Content
                }));
            });

        yield return new ToolDefinition(
            Name: "job_list",
            Category: "jobs",
            Description: "Returns a paginated list of asynchronous jobs (filterable by sessionId or status).",
            IsReadOnly: true,
            IsIdempotent: true,
            SideEffects: "None.",
            Parameters:
            [
                new("sessionId", "string", "Optional session ID filter (omit to list across sessions)."),
                new("status", "string", "Optional status filter: queued | running | succeeded | failed | timed_out | cancelled | interrupted_restart."),
                new("limit", "integer", "Page size (1-100).", Default: 25),
                new("offset", "integer", "Pagination offset.", Default: 0)
            ],
            Handler: (args, _, _) =>
            {
                var list = db.ListJobs(
                    args.OptionalString("sessionId"),
                    args.OptionalString("status"),
                    args.OptionalInt("limit", 25),
                    args.OptionalInt("offset", 0));

                return Task.FromResult(ToolResult.Ok(
                    $"Listed {list.Count} job(s).",
                    new
                    {
                        count = list.Count,
                        activeInProcess = jobs.ActiveJobCount,
                        jobs = list.Select(j => new
                        {
                            jobId = j.Id, j.SessionId, j.Workspace, tool = j.ToolName,
                            j.Status, j.CreatedAtUtc, j.StartedAtUtc, j.CompletedAtUtc, j.ErrorMessage, j.LogExecId
                        })
                    }));
            });

        yield return new ToolDefinition(
            Name: "job_events",
            Category: "jobs",
            Description: "Retrieves incremental persisted events for a job after a given event ID (safe across network reconnects).",
            IsReadOnly: true,
            IsIdempotent: true,
            SideEffects: "None.",
            Parameters:
            [
                new("jobId", "string", "Job identifier.", Required: true),
                new("afterEventId", "integer", "Return only events strictly greater than this ID.", Default: 0),
                new("limit", "integer", "Maximum events returned (1-200).", Default: 100),
                new("waitMs", "integer", "Event-driven wait, 0..25000 milliseconds.", Default: 0),
                new("waitForTerminal", "boolean", "Wait for completion rather than each progress event.", Default: false),
                new("includeResult", "boolean", "Include final result when terminal, avoiding another status call.", Default: false)
            ],
            Handler: async (args, _, ct) =>
            {
                var jobId = args.RequireString("jobId");
                var after = Math.Max(0, args.OptionalLong("afterEventId", 0));
                var limit = args.OptionalInt("limit", 100);
                var wait = Math.Clamp(args.OptionalInt("waitMs", 0), 0, 25_000);
                var waitForTerminal = args.OptionalBool("waitForTerminal", false);
                var includeResult = args.OptionalBool("includeResult", false);
                var job = db.GetJob(jobId, includeResult: false) ?? throw new KeyNotFoundException($"Job '{jobId}' not found.");
                var evts = db.ListJobEvents(jobId, after, limit);
                static bool Terminal(string status) => status is "succeeded" or "failed" or "timed_out" or
                    "cancelled" or "interrupted_restart";
                var start = Stopwatch.GetTimestamp();
                while (!Terminal(job.Status) && (waitForTerminal || evts.Count == 0))
                {
                    var remaining = wait - (int)Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                    if (remaining <= 0) break;
                    // Register before checking durable state to avoid missing the terminal signal.
                    var changed = await jobs.WaitForChangeAsync(jobId, () =>
                    {
                        var latest = db.GetJob(jobId, includeResult: false);
                        return latest is null || Terminal(latest.Status) ||
                            (!waitForTerminal && db.ListJobEvents(jobId, after, 1).Count > 0);
                    }, remaining, ct).ConfigureAwait(false);
                    job = db.GetJob(jobId, includeResult: false) ?? throw new KeyNotFoundException($"Job '{jobId}' expired while waiting.");
                    if (!waitForTerminal || Terminal(job.Status))
                        evts = db.ListJobEvents(jobId, after, limit);
                    if (!changed) break;
                }
                if (waitForTerminal) evts = db.ListJobEvents(jobId, after, limit);
                JsonNode? result = null;
                if (includeResult && Terminal(job.Status))
                {
                    var completed = db.GetJob(jobId);
                    if (!string.IsNullOrWhiteSpace(completed?.ResultJson))
                        result = JsonNode.Parse(completed.ResultJson);
                }
                var lastId = evts.Count > 0 ? evts[^1].Id : after;
                return ToolResult.Ok(
                    $"Retrieved {evts.Count} event(s) for job '{jobId}' (status={job.Status}).",
                    new { jobId, jobStatus = job.Status, lastEventId = lastId, changed = evts.Count > 0,
                        terminal = Terminal(job.Status), count = evts.Count, events = evts, logExecId = job.LogExecId, result });
            });

        yield return new ToolDefinition(
            Name: "job_cancel",
            Category: "jobs",
            Description: "Requests cancellation of an active background job and terminates any child processes owned by that job.",
            IsReadOnly: false,
            IsIdempotent: true,
            SideEffects: "Cancels job and kills its owned child process tree.",
            Parameters:
            [
                new("jobId", "string", "Job identifier to cancel.", Required: true)
            ],
            Handler: (args, _, _) =>
            {
                var jobId = args.RequireString("jobId");
                var job = db.GetJob(jobId) ?? throw new KeyNotFoundException($"Job '{jobId}' not found.");
                var signaled = jobs.CancelJob(jobId);
                return Task.FromResult(ToolResult.Ok(
                    signaled ? $"Sent cancellation signal to job '{jobId}'." : $"Job '{jobId}' is no longer running (status='{job.Status}').",
                    new { jobId, cancelSignaled = signaled, status = job.Status }));
            });

        yield return new ToolDefinition(
            Name: "session_list",
            Category: "sessions",
            Description: "Returns a paginated list of disk-backed LocalIdeBridge sessions.",
            IsReadOnly: true,
            IsIdempotent: true,
            SideEffects: "None.",
            Parameters:
            [
                new("limit", "integer", "Page size (1-100).", Default: 20),
                new("offset", "integer", "Pagination offset.", Default: 0)
            ],
            Handler: (args, _, _) =>
            {
                var sessions = db.ListSessions(args.OptionalInt("limit", 20), args.OptionalInt("offset", 0));
                return Task.FromResult(ToolResult.Ok(
                    $"Listed {sessions.Count} session(s).",
                    new { activeSessionId = db.ActiveSessionId, count = sessions.Count, sessions }));
            });

        yield return new ToolDefinition(
            Name: "session_inspect",
            Category: "sessions",
            Description: "Inspects a session's paginated tool call history, recent edits, jobs, error history, Git checkpoints, and steering note.",
            IsReadOnly: true,
            IsIdempotent: true,
            SideEffects: "None.",
            Parameters:
            [
                new("sessionId", "string", "Session ID to inspect (defaults to active session)."),
                new("toolCallLimit", "integer", "Maximum recent tool calls to include (1-100).", Default: 25),
                new("toolCallOffset", "integer", "Pagination offset for tool calls.", Default: 0)
            ],
            Handler: (args, _, _) =>
            {
                var sid = args.OptionalString("sessionId") ?? db.ActiveSessionId;
                var sess = db.GetSession(sid) ?? throw new KeyNotFoundException($"Session '{sid}' not found.");
                var calls = db.ListToolCalls(sid, args.OptionalInt("toolCallLimit", 25), args.OptionalInt("toolCallOffset", 0));
                var errors = db.ListToolCalls(sid, 15, 0, errorsOnly: true);
                var sessJobs = db.ListJobs(sid, limit: 20);
                var edits = db.ListRecentEdits(sid, 20);
                var checkpoints = db.ListGitCheckpoints(sid, 15);

                return Task.FromResult(ToolResult.Ok(
                    $"Session '{sess.Id}': {sess.ToolCallCount} tool call(s), {sess.ErrorCount} error(s).",
                    new
                    {
                        session = sess,
                        recentToolCalls = calls,
                        recentErrors = errors,
                        jobs = sessJobs.Select(j => new { jobId = j.Id, tool = j.ToolName, j.Status, j.CreatedAtUtc, j.CompletedAtUtc, j.ErrorMessage }),
                        pendingTasks = sessJobs.Where(j => j.Status is "queued" or "running").Select(j => new { jobId = j.Id, tool = j.ToolName, j.Status }),
                        edits = edits.Select(e => new { e.EditId, e.RelativePath, e.Operation, e.LinesAdded, e.LinesRemoved, e.Reverted, e.CreatedAtUtc }),
                        gitCheckpoints = checkpoints
                    }));
            });

        yield return new ToolDefinition(
            Name: "session_switch",
            Category: "sessions",
            Description: "Switches to an existing disk-backed session by sessionId or creates a fresh active session when action='new'.",
            IsReadOnly: false,
            IsIdempotent: false,
            SideEffects: "Changes the active session ID recorded in data/state.db.",
            Parameters:
            [
                new("action", "string", "switch | new", EnumValues: ["switch", "new"], Default: "switch"),
                new("sessionId", "string", "Target session ID when action='switch'.")
            ],
            Handler: (args, _, _) =>
            {
                var action = args.OptionalString("action", "switch")!.ToLowerInvariant();
                var rec = action == "new"
                    ? db.CreateAndActivateSession(guard.ActiveWorkspace ?? "")
                    : db.SwitchSession(args.RequireString("sessionId"));

                return Task.FromResult(ToolResult.Ok(
                    $"Active session is now '{rec.Id}'.",
                    new { session = rec }));
            });

        // ── Workflow Orchestration Tools ───────────────────────────────────────

        yield return new ToolDefinition(
            Name: "workflow_run",
            Category: "workflows",
            Description: "Executes a dependency-aware DAG workflow with parallel independent read-only steps, write-locked sequential modifications, conditional runIf branching, retries, timeouts, and durable checkpoints.",
            IsReadOnly: false,
            IsIdempotent: false,
            SideEffects: "Executes configured workflow steps and persists workflow state in data/state.db.",
            Parameters:
            [
                new("name", "string", "Human-readable workflow name.", Default: "workflow"),
                new("steps", "array", "Ordered list of workflow step objects: { id, tool, arguments?, dependsOn?, runIf?, timeoutSeconds?, retries?, continueOnError?, checkpointAfter?, returnPointers?, returnMaxBytes? }.",
                    Required: true, ItemsSchema: new JsonObject { ["type"] = "object" }),
                new("maxParallelism", "integer", "Optional max concurrency for independent read-only steps."),
                new("async", "boolean", "If true, launches the workflow as a background job and returns jobId immediately.", Default: false)
            ],
            Handler: async (args, ctx, ct) =>
            {
                if (args.OptionalBool("async", false) && ctx.JobId is null)
                {
                    var syncArgs = (JsonObject)args.DeepClone();
                    syncArgs["async"] = false;
                    var job = jobs.StartToolJob("workflow_run", syncArgs, pinnedSessionId: ctx.SessionId, pinnedWorkspace: ctx.WorkspaceRoot);
                    return ToolResult.Ok(
                        $"Started asynchronous workflow job '{job.Id}'.",
                        new { jobId = job.Id, status = job.Status });
                }

                if (!args.TryGetPropertyValue("steps", out var stepsNode) || stepsNode is not JsonArray)
                    throw new ArgumentException("Parameter 'steps' must be a JSON array of step objects.");

                var def = new WorkflowDefinition
                {
                    Name = args.OptionalString("name", "workflow")!,
                    MaxParallelism = args.ContainsKey("maxParallelism") ? args.OptionalInt("maxParallelism", 4) : null,
                    Steps = JsonSerializer.Deserialize<List<WorkflowStepSpec>>(stepsNode.ToJsonString(), AppConfig.JsonOptions) ?? []
                };

                var run = await workflows.ExecuteAsync(def, ctx.SessionId, ctx.ReportProgress, ct);
                return run.Status == "succeeded"
                    ? ToolResult.Ok($"Workflow '{run.Name}' ({run.WorkflowId}) succeeded ({run.SucceededSteps}/{run.TotalSteps} succeeded, {run.SkippedSteps} skipped) in {run.TotalDurationMs}ms.", run)
                    : ToolResult.Fail(run.Status == "cancelled" ? "cancelled" : "workflow_failed",
                        $"Workflow '{run.Name}' ({run.WorkflowId}) ended with status '{run.Status}' ({run.SucceededSteps} succeeded, {run.FailedSteps} failed, {run.SkippedSteps} skipped).", run);
            });

        yield return new ToolDefinition(
            Name: "workflow_status",
            Category: "workflows",
            Description: "Retrieves the persisted execution state and per-step results of a workflow run.",
            IsReadOnly: true,
            IsIdempotent: true,
            SideEffects: "None.",
            Parameters:
            [
                new("workflowId", "string", "Workflow run identifier.", Required: true)
            ],
            Handler: (args, _, _) =>
            {
                var id = args.RequireString("workflowId");
                var rec = db.GetWorkflow(id) ?? throw new KeyNotFoundException($"Workflow '{id}' not found.");
                JsonNode? state = null;
                try { state = JsonNode.Parse(rec.StateJson); } catch { }
                return Task.FromResult(ToolResult.Ok(
                    $"Workflow '{rec.Id}' ({rec.Name}) status='{rec.Status}'.",
                    new { rec.Id, rec.SessionId, rec.Workspace, rec.Name, rec.Status, rec.CreatedAtUtc, rec.UpdatedAtUtc, detail = state }));
            });

        yield return new ToolDefinition(
            Name: "workflow_cancel",
            Category: "workflows",
            Description: "Cancels an actively executing workflow run.",
            IsReadOnly: false,
            IsIdempotent: true,
            SideEffects: "Cancels pending and active steps in the target workflow.",
            Parameters:
            [
                new("workflowId", "string", "Workflow run identifier.", Required: true)
            ],
            Handler: (args, _, _) =>
            {
                var id = args.RequireString("workflowId");
                var cancelled = workflows.Cancel(id);
                return Task.FromResult(ToolResult.Ok(
                    cancelled ? $"Cancellation sent to workflow '{id}'." : $"Workflow '{id}' is not currently running.",
                    new { workflowId = id, cancelSignaled = cancelled }));
            });
    }
}
