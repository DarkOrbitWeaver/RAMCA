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

        if (_isPaused && !tool.IsReadOnly &&
            tool.Name is not ("job_cancel" or "workflow_cancel" or "revert_edit"))
        {
            Interlocked.Increment(ref _totalErrors);
            return ToolResult.Fail("host_paused", "LocalIdeBridge execution is currently paused by the local supervisor. Read-only inspection remains available.");
        }

        var args = arguments ?? new JsonObject();
        var ctx = context ?? new ToolExecutionContext(_db.ActiveSessionId);
        ToolResult result;

        try
        {
            var needsWriteLock = !tool.IsReadOnly &&
                                 !ctx.SkipWriteLock &&
                                 tool.Category is "editing" or "git" or "build" or "execution" &&
                                 _guard.ActiveWorkspace is not null;

            if (needsWriteLock)
            {
                using var _ = await _guard.AcquireWriteLockAsync(_guard.ActiveWorkspace!, ct).ConfigureAwait(false);
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

        var ws = _guard.ActiveWorkspace ?? "";
        _db.RecordToolCall(tool.Name, tool.Category, ws, result.Success, sw.ElapsedMilliseconds, result.Summary, result.ErrorCode, ctx.SessionId);

        if (result.Success)
            _logger.Info(tool.Category, $"{tool.Name}: {result.Summary}", null, sw.ElapsedMilliseconds);
        else
            _logger.Error(tool.Category, $"{tool.Name} failed [{result.ErrorCode}]: {result.ErrorMessage}", null, sw.ElapsedMilliseconds);

        try { OnToolCompleted?.Invoke(tool.Name, result, sw.ElapsedMilliseconds); } catch { }
        return result;
    }
}
