using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text;
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
    public List<string> ReturnPointers { get; set; } = [];
    public int ReturnMaxBytes { get; set; } = 8192;
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
    public Dictionary<string, JsonObject>? Outputs { get; set; }
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
        // Only explicitly referenced JSON pointers are retained. No workflow
        // stores unbounded result payloads in memory or its persisted checkpoints.
        var bindingPointers = CollectBindingPointers(definition);
        var boundValues = new Dictionary<string, Dictionary<string, JsonNode?>>(StringComparer.OrdinalIgnoreCase);
        var totalBoundBytes = 0;
        // Returned projections are transient; do not copy source into every checkpoint.
        var returnedOutputs = new ConcurrentDictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);

        WorkflowRunResult Finish(string status)
        {
            var result = BuildResult(wfId, sid, definition.Name, status, sw.ElapsedMilliseconds, definition.Steps, stepStates);
            if (!returnedOutputs.IsEmpty)
                result.Outputs = definition.Steps.Where(s => returnedOutputs.ContainsKey(s.Id))
                    .ToDictionary(s => s.Id, s => returnedOutputs[s.Id], StringComparer.OrdinalIgnoreCase);
            return result;
        }

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
                            await RunSingleStepAsync(step, stepStates[step.Id], sid, stateLock, onProgress,
                                () => PersistCheckpoint("running"), bindingPointers, boundValues, () => totalBoundBytes,
                                size => totalBoundBytes += size, returnedOutputs, wfCts.Token).ConfigureAwait(false);
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
                        await RunSingleStepAsync(wsStep, stepStates[wsStep.Id], sid, stateLock, onProgress,
                            () => PersistCheckpoint("running"), bindingPointers, boundValues, () => totalBoundBytes,
                            size => totalBoundBytes += size, returnedOutputs, wfCts.Token).ConfigureAwait(false);
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
            return Finish(finalStatus);
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
            return Finish("cancelled");
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
            return Finish("failed");
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
        IReadOnlyDictionary<string, HashSet<string>> neededPointers,
        Dictionary<string, Dictionary<string, JsonNode?>> boundValues,
        Func<int> currentBoundBytes,
        Action<int> addBoundBytes,
        ConcurrentDictionary<string, JsonObject> returnedOutputs,
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

            JsonObject argsClone;
            try
            {
                lock (stateLock)
                    argsClone = spec.Arguments is null ? new JsonObject() :
                        (JsonObject)ReplaceBindings(spec.Arguments, boundValues)!;
            }
            catch (ArgumentException ex)
            {
                lastResult = ToolResult.Fail("workflow_binding_failed", ex.Message);
                break; // Invalid/missing upstream bindings must not trigger a mutation.
            }
            try
            {
                lastResult = await _registry.InvokeAsync(spec.Tool, argsClone,
                    new ToolExecutionContext(sessionId, WorkspaceRoot: _guard.RequireActiveWorkspace()), stepCts.Token).ConfigureAwait(false);
                if (!lastResult.Success && lastResult.ErrorCode == "cancelled" &&
                    stepCts.IsCancellationRequested && !wfCt.IsCancellationRequested)
                    lastResult = ToolResult.Fail("timeout", $"Step '{spec.Id}' exceeded timeout of {spec.TimeoutSeconds}s.");

                if (lastResult.Success && neededPointers.TryGetValue(spec.Id, out var pointers))
                {
                    try
                    {
                        var root = JsonSerializer.SerializeToNode(lastResult, AppConfig.JsonOptions)
                            ?? throw new ArgumentException("Upstream result cannot be serialized for binding.");
                        var outputs = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
                        var bytes = 0;
                        foreach (var pointer in pointers)
                        {
                            var bound = ResolvePointer(root, pointer);
                            var copy = bound?.DeepClone();
                            var byteSize = Encoding.UTF8.GetByteCount(copy?.ToJsonString() ?? "null");
                            if (byteSize > 8192 || bytes + byteSize > 32768)
                                throw new ArgumentException($"Result of '{spec.Id}' exceeds workflow binding output limit.");
                            bytes += byteSize;
                            outputs[pointer] = copy;
                        }
                        lock (stateLock)
                        {
                            if (currentBoundBytes() + bytes > 65536)
                                throw new ArgumentException("Workflow result bindings exceed 64 KiB aggregate limit.");
                            boundValues[spec.Id] = outputs;
                            addBoundBytes(bytes);
                        }
                    }
                    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or JsonException)
                    {
                        // The tool may have written files. Record the ambiguity and
                        // prevent downstream execution; never retry the write.
                        lastResult = ToolResult.Fail("workflow_binding_failed",
                            $"Step '{spec.Id}' executed successfully, but its requested result could not be bound: {ex.Message}. Inspect execution before retrying.",
                            new { toolExecuted = true, lastResult.ExecutionId }, lastResult.ExecutionId);
                    }
                }
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

        // Projection errors do not change the outcome of an already executed tool.
        if (spec.ReturnPointers.Count > 0 && lastResult is not null)
            returnedOutputs[spec.Id] = ProjectResult(lastResult, spec.ReturnPointers, spec.ReturnMaxBytes);

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
        var outputBudget = 0;
        foreach (var s in def.Steps)
        {
            if (string.IsNullOrWhiteSpace(s.Id))
                throw new ArgumentException("Every workflow step must have a non-empty 'id'.");
            if (!ids.Add(s.Id))
                throw new ArgumentException($"Duplicate workflow step id '{s.Id}'.");
            if (string.IsNullOrWhiteSpace(s.Tool) || _registry.Find(s.Tool) is null)
                throw new ArgumentException($"Step '{s.Id}' references unknown tool '{s.Tool}'.");
            if (_registry.Find(s.Tool)?.Name is "workflow_run" or "job_start" or "execute_once" or "workspace_select" or "session_switch")
                throw new ArgumentException("Nested orchestrators and workspace/session changes are not permitted inside workflow steps.");
            if (s.ReturnPointers is null || s.ReturnPointers.Count > 8 ||
                s.ReturnPointers.Any(p => p is null || p.Length is 0 or > 256 || p[0] != '/' || p.Contains('\0')))
                throw new ArgumentException("returnPointers must contain at most eight valid JSON pointers.");
            if (s.ReturnPointers.Count > 0)
            {
                if (s.ReturnMaxBytes is < 512 or > 16384)
                    throw new ArgumentException("returnMaxBytes must be 512..16384.");
                outputBudget += s.ReturnMaxBytes;
                if (outputBudget > 65536)
                    throw new ArgumentException("Workflow return budgets exceed 64 KiB aggregate limit.");
            }
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

        // Bindings must only refer to direct declared dependencies and may
        // never name a sibling or future step. Reject invalid plans before
        // any operation can run.
        _ = CollectBindingPointers(def);

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

    private static (string Step, string Pointer) ParseBinding(JsonObject obj)
    {
        if (obj.Count != 1 || obj["$from"] is not JsonObject from ||
            from.Count != 2 || from["step"]?.GetValueKind() != JsonValueKind.String ||
            from["pointer"]?.GetValueKind() != JsonValueKind.String)
            throw new ArgumentException("A binding must be exactly {\"$from\":{\"step\":\"id\",\"pointer\":\"/data/field\"}}.");
        var step = from["step"]!.GetValue<string>();
        var pointer = from["pointer"]!.GetValue<string>();
        if (step.Length is 0 or > 128 || pointer.Length is 0 or > 256 ||
            pointer[0] != '/' || pointer.Contains('\u0000'))
            throw new ArgumentException("Binding step or JSON pointer is malformed.");
        return (step, pointer);
    }

    private static Dictionary<string, HashSet<string>> CollectBindingPointers(WorkflowDefinition def)
    {
        var needed = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var count = 0;
        foreach (var step in def.Steps)
        {
            void Visit(JsonNode? node, int depth)
            {
                if (depth > 20) throw new ArgumentException("Workflow binding nesting exceeds 20 levels.");
                if (node is JsonObject obj)
                {
                    if (obj.ContainsKey("$from"))
                    {
                        var (refStep, pointer) = ParseBinding(obj);
                        if (!step.DependsOn.Contains(refStep, StringComparer.OrdinalIgnoreCase))
                            throw new ArgumentException($"Step '{step.Id}' must declare dependency '{refStep}' before using its output.");
                        if (++count > 256) throw new ArgumentException("Workflow has over 256 references.");
                        if (!needed.TryGetValue(refStep, out var pointers))
                        {
                            pointers = new HashSet<string>(StringComparer.Ordinal);
                            needed[refStep] = pointers;
                        }
                        pointers.Add(pointer);
                    }
                    else foreach (var pair in obj) Visit(pair.Value, depth + 1);
                }
                else if (node is JsonArray arr)
                    foreach (var item in arr) Visit(item, depth + 1);
            }
            Visit(step.Arguments, 0);
        }
        return needed;
    }

    private static JsonNode? ReplaceBindings(JsonNode? node,
        IReadOnlyDictionary<string, Dictionary<string, JsonNode?>> values)
    {
        if (node is JsonObject obj)
        {
            if (obj.ContainsKey("$from"))
            {
                var (step, pointer) = ParseBinding(obj);
                if (!values.TryGetValue(step, out var exports) ||
                    !exports.TryGetValue(pointer, out var value))
                    throw new ArgumentException($"Result '{pointer}' of dependency '{step}' is not available.");
                return value?.DeepClone();
            }
            var output = new JsonObject();
            foreach (var pair in obj)
                output[pair.Key] = ReplaceBindings(pair.Value, values);
            return output;
        }
        if (node is JsonArray arr)
            return new JsonArray(arr.Select(n => ReplaceBindings(n, values)).ToArray());
        return node?.DeepClone();
    }

    private static JsonObject ProjectResult(ToolResult result, IReadOnlyList<string> pointers, int budget)
    {
        var values = new JsonObject();
        var errors = new JsonArray();
        var projection = new JsonObject { ["values"] = values, ["errors"] = errors };
        try
        {
            var root = JsonSerializer.SerializeToNode(result, AppConfig.JsonOptions)
                ?? throw new ArgumentException("Result cannot be serialized.");
            foreach (var pointer in pointers.Distinct(StringComparer.Ordinal))
            {
                try
                {
                    values[pointer] = ResolvePointer(root, pointer)?.DeepClone();
                    if (JsonSerializer.SerializeToUtf8Bytes(projection, AppConfig.JsonOptions).Length <= budget)
                        continue;
                    values.Remove(pointer);
                    errors.Add(new JsonObject { ["pointer"] = pointer, ["code"] = "output_budget_exceeded" });
                }
                catch (ArgumentException)
                {
                    errors.Add(new JsonObject { ["pointer"] = pointer, ["code"] = "pointer_not_found" });
                }
                if (JsonSerializer.SerializeToUtf8Bytes(projection, AppConfig.JsonOptions).Length > budget)
                    return new JsonObject { ["error"] = "output_budget_exceeded" };
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or JsonException or NotSupportedException)
        {
            return new JsonObject { ["error"] = "output_projection_failed" };
        }
        return projection;
    }

    private static JsonNode? ResolvePointer(JsonNode root, string pointer)
    {
        JsonNode? current = root;
        foreach (var segment in pointer[1..].Split('/'))
        {
            var key = segment.Replace("~1", "/", StringComparison.Ordinal)
                .Replace("~0", "~", StringComparison.Ordinal);
            if (current is JsonObject obj)
            {
                if (!obj.TryGetPropertyValue(key, out current))
                    throw new ArgumentException($"Requested result pointer '{pointer}' does not exist.");
            }
            else if (current is JsonArray arr &&
                int.TryParse(key, out var index) && index >= 0 && index < arr.Count)
                current = arr[index];
            else throw new ArgumentException($"Requested result pointer '{pointer}' is not available.");
        }
        return current;
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
