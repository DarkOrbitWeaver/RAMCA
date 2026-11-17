using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using LocalIdeBridge.Core;
using LocalIdeBridge.Storage;
using LocalIdeBridge.Tools;

namespace LocalIdeBridge.Workflows;

public sealed class WorkflowStepSpec
{
    public string Id { get; set; } = "";
    public string Tool { get; set; } = "";
    public JsonObject? Arguments { get; set; }
    public List<string> DependsOn { get; set; } = [];
    /// <summary>on_success (default) | on_failure | always | step_succeeded:&lt;id&gt; | step_failed:&lt;id&gt;</summary>
    public string RunIf { get; set; } = "on_success";
    public int? TimeoutSeconds { get; set; }
    public int Retries { get; set; }
    public int RetryDelayMs { get; set; } = 300;
    public bool ContinueOnError { get; set; }
    public bool CheckpointAfter { get; set; }
}

public sealed class WorkflowDefinition
{
    public string Name { get; set; } = "workflow";
    public List<WorkflowStepSpec> Steps { get; set; } = [];
    public int? MaxParallelism { get; set; }
}

public sealed class WorkflowStepState
{
    public string Id { get; set; } = "";
    public string Tool { get; set; } = "";
    public bool IsReadOnly { get; set; }
    public string Status { get; set; } = "pending"; // pending | running | succeeded | failed | skipped | cancelled
    public int Attempts { get; set; }
    public long DurationMs { get; set; }
    public string? Summary { get; set; }
    public string? ErrorCode { get; set; }
    public string? ExecutionId { get; set; }
}

public sealed class WorkflowRunResult
{
    public string WorkflowId { get; set; } = "";
    public string SessionId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Status { get; set; } = "";
    public long TotalDurationMs { get; set; }
    public int TotalSteps { get; set; }
    public int SucceededSteps { get; set; }
    public int FailedSteps { get; set; }
    public int SkippedSteps { get; set; }
    public List<WorkflowStepState> Steps { get; set; } = [];
}

/// <summary>
/// Deterministic, bounded-concurrency DAG workflow engine.
/// Executes independent read-only steps in parallel, serializes modifying steps under the
/// workspace write lock, evaluates conditional runIf rules, supports safe retries, timeouts,
/// cancellation, and durable SQLite/Git checkpoints.
/// </summary>
public sealed class WorkflowEngine
{
    private readonly AppConfig _config;
    private readonly WorkspaceGuard _guard;
    private readonly StateDatabase _db;
    private readonly ToolRegistry _registry;
    private readonly RedactingLogger _logger;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _active = new(StringComparer.OrdinalIgnoreCase);

    public WorkflowEngine(
        AppConfig config,
        WorkspaceGuard guard,
        StateDatabase db,
        ToolRegistry registry,
        RedactingLogger logger)
    {
        _config = config;
        _guard = guard;
        _db = db;
        _registry = registry;
        _logger = logger;
    }

    public async Task<WorkflowRunResult> ExecuteAsync(
        WorkflowDefinition definition,
        string? sessionId = null,
        Action<string>? onProgress = null,
        CancellationToken ct = default)
    {
        ValidateDag(definition);

        var wfId = $"wf_{DateTimeOffset.UtcNow:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString("N")[..8]}";
        var sid = sessionId ?? _db.ActiveSessionId;
        var ws = _guard.RequireActiveWorkspace();
        using var invocationScope = _guard.UseInvocationWorkspace(ws);
        var createdAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();

        using var wfCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _active[wfId] = wfCts;

        var stepStates = new Dictionary<string, WorkflowStepState>(StringComparer.OrdinalIgnoreCase);
        foreach (var step in definition.Steps)
        {
            var tool = _registry.Find(step.Tool)!;
            stepStates[step.Id] = new WorkflowStepState
            {
                Id = step.Id,
                Tool = step.Tool,
                IsReadOnly = tool.IsReadOnly,
                Status = "pending"
            };
        }

        var stateLock = new object();

        void PersistCheckpoint(string status)
        {
            lock (stateLock)
            {
                var snapshot = BuildResult(wfId, sid, definition.Name, status, sw.ElapsedMilliseconds, definition.Steps, stepStates);
                var json = JsonSerializer.Serialize(snapshot, AppConfig.JsonOptions);
                _db.SaveWorkflow(new WorkflowRecord(
                    wfId, sid, ws, definition.Name, status,
                    snapshot.TotalSteps, snapshot.SucceededSteps, snapshot.FailedSteps,
                    json, createdAt, DateTimeOffset.UtcNow));
            }
        }

        PersistCheckpoint("running");
        _logger.Info("workflow", $"Started workflow '{definition.Name}' ({wfId}) with {definition.Steps.Count} step(s).");

        var maxPar = Math.Clamp(definition.MaxParallelism ?? _config.Resources.MaxParallelReadSteps, 1, _config.Resources.MaxParallelReadSteps);
        using var readGate = new SemaphoreSlim(maxPar, maxPar);
        using var writeGate = new SemaphoreSlim(1, 1);

        try
        {
            var remaining = new HashSet<string>(definition.Steps.Select(s => s.Id), StringComparer.OrdinalIgnoreCase);
            var specMap = definition.Steps.ToDictionary(s => s.Id, StringComparer.OrdinalIgnoreCase);

            while (remaining.Count > 0)
            {
                wfCts.Token.ThrowIfCancellationRequested();

                // Collect all steps whose dependencies are now terminal.
                // Global on_failure handlers have no dependencies, but must run only
                // after ordinary steps have finished, not in the first wave.
                static bool IsGlobalFailureHandler(WorkflowStepSpec s) =>
                    s.DependsOn.Count == 0 &&
                    string.Equals(s.RunIf?.Trim(), "on_failure", StringComparison.OrdinalIgnoreCase);
                bool DependsOnGlobalHandler(string stepId, HashSet<string> seen)
                {
                    if (!seen.Add(stepId)) return false;
                    return specMap[stepId].DependsOn.Any(d => IsGlobalFailureHandler(specMap[d]) ||
                        DependsOnGlobalHandler(d, seen));
                }
                // A dependent step must not prevent its own global failure
                // handler from becoming runnable (the old rule deadlocked).
                var outstandingIndependent = remaining.Any(id =>
                    !IsGlobalFailureHandler(specMap[id]) &&
                    !DependsOnGlobalHandler(id, new HashSet<string>(StringComparer.OrdinalIgnoreCase)));
                var ready = remaining
                    .Select(id => specMap[id])
                    .Where(s => s.DependsOn.All(d => !remaining.Contains(d)))
                    .Where(s => !IsGlobalFailureHandler(s) || !outstandingIndependent)
                    .ToList();

                if (ready.Count == 0)
                    throw new InvalidOperationException("Workflow deadlock detected (cyclic or unresolved dependencies).");

                var toExecute = new List<WorkflowStepSpec>();
                foreach (var step in ready)
                {
                    if (!ShouldStepRun(step, stepStates, specMap))
                    {
                        lock (stateLock)
                        {
                            stepStates[step.Id].Status = "skipped";
                            stepStates[step.Id].Summary = $"Skipped due to condition runIf='{step.RunIf}' or failed prerequisite.";
                        }
                        remaining.Remove(step.Id);
                    }
                    else
                    {
                        toExecute.Add(step);
                    }
                }

                if (toExecute.Count == 0)
                {
                    PersistCheckpoint("running");
                    continue;
                }

                // Run independent read-only steps concurrently; run write steps sequentially in defined order.
                var readSteps = toExecute.Where(s => stepStates[s.Id].IsReadOnly).ToList();
                var writeSteps = toExecute.Where(s => !stepStates[s.Id].IsReadOnly).ToList();

                if (readSteps.Count > 0)
                {
                    var tasks = readSteps.Select(async step =>
                    {
                        await readGate.WaitAsync(wfCts.Token).ConfigureAwait(false);
                        try
                        {
                            await RunSingleStepAsync(step, stepStates[step.Id], sid, stateLock, onProgress, () => PersistCheckpoint("running"), wfCts.Token).ConfigureAwait(false);
                        }
                        finally
                        {
                            readGate.Release();
                        }
                    });
                    await Task.WhenAll(tasks).ConfigureAwait(false);
                    foreach (var rs in readSteps) remaining.Remove(rs.Id);
                    PersistCheckpoint("running");
                }

                // Execute at most the first ready write step per wave so newly unlocked read steps can fan out right after.
                if (writeSteps.Count > 0)
                {
                    var wsStep = writeSteps[0];
                    await writeGate.WaitAsync(wfCts.Token).ConfigureAwait(false);
                    try
                    {
                        await RunSingleStepAsync(wsStep, stepStates[wsStep.Id], sid, stateLock, onProgress, () => PersistCheckpoint("running"), wfCts.Token).ConfigureAwait(false);
                    }
                    finally
                    {
                        writeGate.Release();
                    }
                    remaining.Remove(wsStep.Id);
                    PersistCheckpoint("running");
                }
            }

            sw.Stop();
            var anyUnhandledFailure = definition.Steps.Any(s =>
                stepStates[s.Id].Status == "failed" && !s.ContinueOnError);
            var finalStatus = anyUnhandledFailure ? "failed" : "succeeded";
            PersistCheckpoint(finalStatus);
            _logger.Info("workflow", $"Workflow '{wfId}' completed with status '{finalStatus}'.", null, sw.ElapsedMilliseconds);
            return BuildResult(wfId, sid, definition.Name, finalStatus, sw.ElapsedMilliseconds, definition.Steps, stepStates);
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            lock (stateLock)
            {
                foreach (var st in stepStates.Values.Where(v => v.Status is "pending" or "running"))
                    st.Status = "cancelled";
            }
            PersistCheckpoint("cancelled");
            return BuildResult(wfId, sid, definition.Name, "cancelled", sw.ElapsedMilliseconds, definition.Steps, stepStates);
        }
        catch (Exception ex)
        {
            sw.Stop();
            lock (stateLock)
            {
                foreach (var state in stepStates.Values.Where(s => s.Status is "running" or "pending"))
                {
                    state.Status = "failed";
                    state.ErrorCode = "workflow_execution_error";
                    state.Summary = $"{ex.GetType().Name}: {ex.Message}";
                }
            }
            PersistCheckpoint("failed");
            _logger.Error("workflow", $"Workflow '{wfId}' failed: {ex.GetType().Name}: {ex.Message}");
            return BuildResult(wfId, sid, definition.Name, "failed", sw.ElapsedMilliseconds, definition.Steps, stepStates);
        }
        finally
        {
            _active.TryRemove(wfId, out _);
        }
    }

    public bool Cancel(string workflowId)
    {
        if (_active.TryGetValue(workflowId, out var cts))
        {
            try { cts.Cancel(); } catch { }
            return true;
        }
        return false;
    }

    private async Task RunSingleStepAsync(
        WorkflowStepSpec spec,
        WorkflowStepState state,
        string sessionId,
        object stateLock,
        Action<string>? onProgress,
        Action saveCheckpoint,
        CancellationToken wfCt)
    {
        var tool = _registry.Find(spec.Tool)!;
        // Retrying an edit/command/commit after a timeout can execute it TWICE.
        // Retry only explicit idempotent operations; the caller can inspect and resume.
        var maxAttempts = 1 + (tool.IsReadOnly ? Math.Clamp(spec.Retries, 0, 5) : 0);
        var stepSw = Stopwatch.StartNew();
        ToolResult? lastResult = null;

        lock (stateLock) state.Status = "running";
        onProgress?.Invoke($"Step '{spec.Id}' ({spec.Tool}) started.");

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            wfCt.ThrowIfCancellationRequested();
            lock (stateLock) state.Attempts = attempt;

            using var stepCts = CancellationTokenSource.CreateLinkedTokenSource(wfCt);
            if (spec.TimeoutSeconds is > 0)
                stepCts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(spec.TimeoutSeconds.Value, 1, _config.Resources.MaxCommandTimeoutSeconds)));

            var argsClone = spec.Arguments is null ? new JsonObject() : (JsonObject)spec.Arguments.DeepClone();
            try
            {
                lastResult = await _registry.InvokeAsync(spec.Tool, argsClone,
                    new ToolExecutionContext(sessionId, WorkspaceRoot: _guard.RequireActiveWorkspace()), stepCts.Token).ConfigureAwait(false);
                if (!lastResult.Success && lastResult.ErrorCode == "cancelled" &&
                    stepCts.IsCancellationRequested && !wfCt.IsCancellationRequested)
                    lastResult = ToolResult.Fail("timeout", $"Step '{spec.Id}' exceeded timeout of {spec.TimeoutSeconds}s.");
            }
            catch (OperationCanceledException) when (!wfCt.IsCancellationRequested)
            {
                lastResult = ToolResult.Fail("timeout", $"Step '{spec.Id}' exceeded timeout of {spec.TimeoutSeconds}s.");
            }

            if (lastResult.Success)
                break;

            if (lastResult.ErrorCode == "cancelled" && wfCt.IsCancellationRequested)
                wfCt.ThrowIfCancellationRequested();

            if (attempt < maxAttempts)
            {
                var delay = Math.Clamp(spec.RetryDelayMs * attempt, 50, 10_000);
                onProgress?.Invoke($"Step '{spec.Id}' attempt {attempt}/{maxAttempts} failed ({lastResult.ErrorCode}); retrying in {delay}ms.");
                await Task.Delay(delay, wfCt).ConfigureAwait(false);
            }
        }

        stepSw.Stop();
        lock (stateLock)
        {
            state.DurationMs = stepSw.ElapsedMilliseconds;
            state.Status = lastResult?.Success == true ? "succeeded" : "failed";
            state.Summary = lastResult?.Summary;
            state.ErrorCode = lastResult?.ErrorCode;
            state.ExecutionId = lastResult?.ExecutionId;
        }

        onProgress?.Invoke($"Step '{spec.Id}' {state.Status} in {stepSw.ElapsedMilliseconds}ms: {state.Summary}");

        if (spec.CheckpointAfter && lastResult?.Success == true)
        {
            // Best-effort Git checkpoint if workspace is a Git repository with a HEAD commit.
            try
            {
                var checkpoint = await _registry.InvokeAsync("git_checkpoint", new JsonObject { ["label"] = $"wf:{spec.Id}" },
                    new ToolExecutionContext(sessionId), wfCt).ConfigureAwait(false);
                if (!checkpoint.Success)
                {
                    state.Status = "failed";
                    state.ErrorCode = "checkpoint_failed";
                    state.Summary = checkpoint.ErrorMessage;
                }
            }
            catch (Exception ex)
            {
                state.Status = "failed";
                state.ErrorCode = "checkpoint_failed";
                state.Summary = ex.Message;
            }
            saveCheckpoint();
        }
    }

    private static bool ShouldStepRun(
        WorkflowStepSpec step,
        IReadOnlyDictionary<string, WorkflowStepState> states,
        IReadOnlyDictionary<string, WorkflowStepSpec> specs)
    {
        var cond = (step.RunIf ?? "on_success").Trim();

        if (cond.Equals("always", StringComparison.OrdinalIgnoreCase))
            return true;

        if (cond.StartsWith("step_succeeded:", StringComparison.OrdinalIgnoreCase))
        {
            var refId = cond["step_succeeded:".Length..].Trim();
            return states.TryGetValue(refId, out var s) && s.Status == "succeeded";
        }

        if (cond.StartsWith("step_failed:", StringComparison.OrdinalIgnoreCase))
        {
            var refId = cond["step_failed:".Length..].Trim();
            return states.TryGetValue(refId, out var s) && s.Status == "failed";
        }

        if (cond.Equals("on_failure", StringComparison.OrdinalIgnoreCase))
        {
            if (step.DependsOn.Count > 0)
                return step.DependsOn.Any(d => states[d].Status == "failed");
            return states.Values.Any(s => s.Status == "failed");
        }

        if (!cond.Equals("on_success", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"Unsupported workflow runIf condition '{cond}'.");

        // Default: on_success — every direct dependency must have succeeded (or failed with continueOnError=true).
        foreach (var dep in step.DependsOn)
        {
            var st = states[dep];
            if (st.Status == "succeeded") continue;
            if (st.Status == "failed" && specs[dep].ContinueOnError) continue;
            return false;
        }
        return true;
    }

    private void ValidateDag(WorkflowDefinition def)
    {
        if (def.Steps.Count == 0)
            throw new ArgumentException("Workflow must contain at least one step.");
        if (def.Steps.Count > 64)
            throw new ArgumentException("Workflow exceeds maximum of 64 steps.");

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in def.Steps)
        {
            if (string.IsNullOrWhiteSpace(s.Id))
                throw new ArgumentException("Every workflow step must have a non-empty 'id'.");
            if (!ids.Add(s.Id))
                throw new ArgumentException($"Duplicate workflow step id '{s.Id}'.");
            if (string.IsNullOrWhiteSpace(s.Tool) || _registry.Find(s.Tool) is null)
                throw new ArgumentException($"Step '{s.Id}' references unknown tool '{s.Tool}'.");
            if (s.Tool is "workflow_run" or "job_start" or "execute_once")
                throw new ArgumentException("Nested orchestrators are not permitted inside workflow steps; use declared DAG dependencies.");
            var cond = (s.RunIf ?? "on_success").Trim();
            if (!(cond.Equals("on_success", StringComparison.OrdinalIgnoreCase) ||
                  cond.Equals("on_failure", StringComparison.OrdinalIgnoreCase) ||
                  cond.Equals("always", StringComparison.OrdinalIgnoreCase) ||
                  cond.StartsWith("step_succeeded:", StringComparison.OrdinalIgnoreCase) ||
                  cond.StartsWith("step_failed:", StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException($"Unknown workflow runIf condition: '{cond}'.");
        }

        foreach (var s in def.Steps)
        {
            foreach (var dep in s.DependsOn)
            {
                if (!ids.Contains(dep))
                    throw new ArgumentException($"Step '{s.Id}' depends on unknown step '{dep}'.");
                if (string.Equals(dep, s.Id, StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException($"Step '{s.Id}' cannot depend on itself.");
            }
        }

        foreach (var s in def.Steps)
        {
            var condition = (s.RunIf ?? "").Trim();
            var colon = condition.IndexOf(':');
            if (colon < 0) continue;
            var referenced = condition[(colon + 1)..].Trim();
            if (!ids.Contains(referenced) || !s.DependsOn.Contains(referenced, StringComparer.OrdinalIgnoreCase))
                throw new ArgumentException($"Step '{s.Id}' condition must reference one of its declared dependencies.");
        }

        // Cycle detection via 3-color DFS.
        var map = def.Steps.ToDictionary(s => s.Id, StringComparer.OrdinalIgnoreCase);
        var color = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        void Visit(string id)
        {
            color[id] = 1;
            foreach (var dep in map[id].DependsOn)
            {
                var c = color.GetValueOrDefault(dep, 0);
                if (c == 1) throw new ArgumentException($"Workflow contains a dependency cycle involving '{id}' -> '{dep}'.");
                if (c == 0) Visit(dep);
            }
            color[id] = 2;
        }

        foreach (var s in def.Steps)
        {
            if (color.GetValueOrDefault(s.Id, 0) == 0)
                Visit(s.Id);
        }
    }

    private static WorkflowRunResult BuildResult(
        string wfId,
        string sessionId,
        string name,
        string status,
        long durationMs,
        IReadOnlyList<WorkflowStepSpec> orderedSpecs,
        IReadOnlyDictionary<string, WorkflowStepState> states)
    {
        var list = orderedSpecs.Select(s => states[s.Id]).ToList();
        return new WorkflowRunResult
        {
            WorkflowId = wfId,
            SessionId = sessionId,
            Name = name,
            Status = status,
            TotalDurationMs = durationMs,
            TotalSteps = list.Count,
            SucceededSteps = list.Count(s => s.Status == "succeeded"),
            FailedSteps = list.Count(s => s.Status == "failed"),
            SkippedSteps = list.Count(s => s.Status == "skipped"),
            Steps = list
        };
    }
}
