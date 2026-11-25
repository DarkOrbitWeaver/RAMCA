using System.Diagnostics;
using System.Text.Json.Nodes;
using LocalIdeBridge.Core;
using LocalIdeBridge.Storage;

namespace LocalIdeBridge.Tools;

/// <summary>
/// Single authoritative tool execution engine shared across MCP, HTTPS API, CLI, TUI, and workflows.
/// Enforces pause state, workspace write locks, structured error mapping, bounded audit persistence,
/// and credential redaction.
/// </summary>
public sealed record ToolCompletionReceipt(
    long CallId, string SessionId, string Workspace, string ToolName,
    ToolResult Result, long DurationMs, DateTimeOffset CompletedAtUtc);

public sealed class ToolRegistry
{
    private readonly Dictionary<string, ToolDefinition> _tools = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ToolDefinition> _ordered = [];
    private readonly WorkspaceGuard _guard;
    private readonly StateDatabase _db;
    private readonly RedactingLogger _logger;
    private readonly CredentialStore _credentials;
    private volatile bool _isPaused;
    private long _totalInvocations;
    private long _totalErrors;

    public bool IsPaused
    {
        get => _isPaused;
        set
        {
            _isPaused = value;
            _logger.Info("supervisor", value ? "Execution paused by local user." : "Execution resumed by local user.");
        }
    }

    public long TotalInvocations => Interlocked.Read(ref _totalInvocations);
    public long TotalErrors => Interlocked.Read(ref _totalErrors);
    public IReadOnlyList<ToolDefinition> All => _ordered;

    public event Action<string, ToolResult, long>? OnToolCompleted;
    public event Action<ToolCompletionReceipt>? OnToolCompletedExact;

    public ToolRegistry(WorkspaceGuard guard, StateDatabase db, RedactingLogger logger, CredentialStore credentials)
    {
        _guard = guard;
        _db = db;
        _logger = logger;
        _credentials = credentials;
    }

    public void Register(ToolDefinition tool)
    {
        if (_tools.ContainsKey(tool.Name))
            throw new InvalidOperationException($"Duplicate tool registration: {tool.Name}");
        _tools[tool.Name] = tool;
        _ordered.Add(tool);
    }

    public void RegisterRange(IEnumerable<ToolDefinition> tools)
    {
        foreach (var t in tools) Register(t);
    }

    public ToolDefinition? Find(string name)
        => _tools.TryGetValue(name, out var def) ? def : null;

    // Shared remote-call entry point. An operation ID belongs to the logical
    // action, not the JSON-RPC request ID (which clients may reuse).
    public Task<ToolResult> InvokeClientAsync(
        string toolName, JsonObject? arguments, string? workspace, string? operationId,
        CancellationToken ct = default)
    {
        string? root;
        try
        {
            root = workspace is null ? _guard.ActiveWorkspace : _guard.ResolveTrustedWorkspace(workspace);
        }
        catch (WorkspaceViolationException ex)
        {
            return Task.FromResult(ToolResult.Fail("workspace_violation", _credentials.Redact(ex.Message)));
        }
        var context = new ToolExecutionContext(_db.ActiveSessionId, WorkspaceRoot: root);
        var definition = Find(toolName);
        if (definition is null)
            return Task.FromResult(ToolResult.Fail("unknown_tool", $"Unknown tool '{toolName}'."));
        if (operationId is null)
            return InvokeAsync(definition.Name, arguments, context, ct);
        if (root is null)
            return Task.FromResult(ToolResult.Fail("workspace_violation", "Select a trusted workspace before using an operation ID."));
        if (definition.Name is "execute_once" or "workspace_select" or "session_switch")
            return Task.FromResult(ToolResult.Fail("invalid_arguments", "Operation IDs cannot wrap workspace/session selection or execute_once."));
        var envelope = new JsonObject
        {
            ["operationId"] = operationId,
            ["tool"] = definition.Name,
            ["arguments"] = arguments?.DeepClone() ?? new JsonObject()
        };
        return InvokeAsync("execute_once", envelope, context, ct);
    }

    public async Task<ToolResult> InvokeAsync(
        string toolName,
        JsonObject? arguments,
        ToolExecutionContext? context = null,
        CancellationToken ct = default)
    {
        Interlocked.Increment(ref _totalInvocations);
        var sw = Stopwatch.StartNew();

        if (!_tools.TryGetValue(toolName, out var tool))
        {
            Interlocked.Increment(ref _totalErrors);
            return ToolResult.Fail("unknown_tool", $"Unknown tool '{toolName}'. Discover available tools via tools/list or GET /api/v1/tools.");
        }

        var args = arguments ?? new JsonObject();
        // Build-mode diagnostics may write bin/obj. Explicit Roslyn mode is read-only.
        var diagnosticRoslyn = tool.Name.Equals("compiler_diagnostics", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(args.OptionalString("mode", "build"), "roslyn", StringComparison.OrdinalIgnoreCase);
        var boardRead = (tool.Name == "board_task" || tool.Name == "board_pin") &&
            string.Equals(args.OptionalString("action"), "list", StringComparison.OrdinalIgnoreCase);
        var effectiveReadOnly = tool.IsReadOnly || diagnosticRoslyn || boardRead;
        if (_isPaused && !effectiveReadOnly &&
            tool.Name is not ("job_cancel" or "workflow_cancel" or "revert_edit"))
        {
            Interlocked.Increment(ref _totalErrors);
            return ToolResult.Fail("host_paused", "LocalIdeBridge execution is currently paused by the local supervisor. Read-only inspection remains available.");
        }

        var ctx = context ?? new ToolExecutionContext(_db.ActiveSessionId);
        using var sessionScope = _db.UseInvocationSession(ctx.SessionId);
        var workspace = ctx.WorkspaceRoot ?? _guard.ActiveWorkspace;
        ToolResult result;

        try
        {
            using var workspaceScope = workspace is null ? null : _guard.UseInvocationWorkspace(workspace);
            var needsWriteLock = !effectiveReadOnly && !ctx.SkipWriteLock &&
                workspace is not null &&
                (tool.Category is "editing" or "git" or "build" or "execution" or "test" ||
                 tool.Name.Equals("compiler_diagnostics", StringComparison.OrdinalIgnoreCase) ||
                 tool.Name is "board_join" or "board_task");

            if (needsWriteLock)
            {
                using var _ = await _guard.AcquireWriteLockAsync(workspace!, ct).ConfigureAwait(false);
                if (_isPaused && !effectiveReadOnly && tool.Name is not ("job_cancel" or "workflow_cancel" or "revert_edit"))
                    return ToolResult.Fail("host_paused", "The supervisor paused execution while this operation waited for the write lock.");
                result = await tool.Handler(args, ctx, ct).ConfigureAwait(false);
            }
            else
            {
                result = await tool.Handler(args, ctx, ct).ConfigureAwait(false);
            }
        }
        catch (WorkspaceViolationException ex)
        {
            result = ToolResult.Fail("workspace_violation", _credentials.Redact(ex.Message));
        }
        catch (ArgumentException ex)
        {
            result = ToolResult.Fail("invalid_arguments", _credentials.Redact(ex.Message));
        }
        catch (FileNotFoundException ex)
        {
            result = ToolResult.Fail("not_found", _credentials.Redact(ex.Message));
        }
        catch (DirectoryNotFoundException ex)
        {
            result = ToolResult.Fail("not_found", _credentials.Redact(ex.Message));
        }
        catch (KeyNotFoundException ex)
        {
            result = ToolResult.Fail("not_found", _credentials.Redact(ex.Message));
        }
        catch (UnauthorizedAccessException ex)
        {
            result = ToolResult.Fail("authorization_required", _credentials.Redact(ex.Message));
        }
        catch (OperationCanceledException)
        {
            result = ToolResult.Fail("cancelled", $"Tool '{tool.Name}' was cancelled.");
        }
        catch (Exception ex)
        {
            result = ToolResult.Fail("execution_error", _credentials.Redact($"{ex.GetType().Name}: {ex.Message}"));
        }
        finally
        {
            sw.Stop();
        }

        if (!result.Success)
            Interlocked.Increment(ref _totalErrors);

        var ws = workspace ?? "";
        long recordedCallId;
        try
        {
            recordedCallId = _db.RecordToolCall(tool.Name, tool.Category, ws, result.Success, sw.ElapsedMilliseconds, result.Summary, result.ErrorCode, ctx.SessionId);
        }
        catch (Exception ex)
        {
            // The handler has already run. Do not hide that result behind an
            // exception and invite blind replay of a potentially committed edit.
            var message = _credentials.Redact(ex.Message);
            try { _logger.Error("audit", $"Tool '{tool.Name}' executed, but its call record failed: {message}"); } catch { }
            return ToolResult.Fail("audit_outcome_unrecorded",
                $"Tool '{tool.Name}' already executed, but its audit record could not be saved. Do NOT retry automatically. Original success={result.Success}. {message}",
                new { executedResult = result }, result.ExecutionId);
        }

        if (result.Success)
            _logger.Info(tool.Category, $"{tool.Name}: {result.Summary}", null, sw.ElapsedMilliseconds);
        else
            _logger.Error(tool.Category, $"{tool.Name} failed [{result.ErrorCode}]: {result.ErrorMessage}", null, sw.ElapsedMilliseconds);

        try { OnToolCompletedExact?.Invoke(new ToolCompletionReceipt(
            recordedCallId, ctx.SessionId, ws, tool.Name, result, sw.ElapsedMilliseconds, DateTimeOffset.UtcNow)); } catch { }
        try { OnToolCompleted?.Invoke(tool.Name, result, sw.ElapsedMilliseconds); } catch { }
        return result;
    }
}
