using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using LocalIdeBridge.Core;
using LocalIdeBridge.Storage;
using LocalIdeBridge.Tools;

namespace LocalIdeBridge.Workflows;

/// <summary>
/// Bounded-concurrency asynchronous job supervisor with durable SQLite state,
/// incremental event persistence, cancellation, and clean host shutdown.
/// </summary>
public sealed class JobManager : IDisposable
{
    private sealed record RunningJob(string JobId, CancellationTokenSource Cts, Task Task);

    private readonly AppConfig _config;
    private readonly WorkspaceGuard _guard;
    private readonly StateDatabase _db;
    private readonly ToolRegistry _registry;
    private readonly RedactingLogger _logger;
    private readonly SemaphoreSlim _concurrencyGate;
    private readonly CancellationTokenSource _hostCts = new();
    private readonly ConcurrentDictionary<string, RunningJob> _active = new(StringComparer.OrdinalIgnoreCase);
    private int _disposed;

    public int ActiveJobCount => _active.Count;

    public JobManager(
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
        _concurrencyGate = new SemaphoreSlim(config.Resources.MaxConcurrentJobs, config.Resources.MaxConcurrentJobs);
    }

    public JobRecord StartToolJob(string toolName, JsonObject? arguments, int? timeoutSeconds = null)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        var tool = _registry.Find(toolName)
            ?? throw new ArgumentException($"Unknown tool '{toolName}'.");
        if (tool.Name.Equals("job_start", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Cannot recursively nest job_start inside job_start.");

        var jobId = $"job_{DateTimeOffset.UtcNow:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString("N")[..8]}";
        var sessionId = _db.ActiveSessionId;
        var ws = _guard.ActiveWorkspace ?? "";
        var clonedArgs = arguments is null ? new JsonObject() : (JsonObject)arguments.DeepClone();

        _db.CreateJob(jobId, sessionId, ws, tool.Name, clonedArgs.ToJsonString(), tool.IsIdempotent);

        // An explicit timeout is not the same as the user cancelling a job.
        // Keep their cancellation causes separate in the persisted job status.
        var timeoutCts = new CancellationTokenSource();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_hostCts.Token, timeoutCts.Token);
        if (timeoutSeconds is > 0)
        {
            var clamped = Math.Clamp(timeoutSeconds.Value, 1, _config.Resources.MaxCommandTimeoutSeconds);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(clamped));
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _active[jobId] = new RunningJob(jobId, cts, completion.Task);
        _ = Task.Run(async () =>
        {
            try { await ExecuteJobAsync(jobId, sessionId, ws, tool.Name, clonedArgs, cts, timeoutCts).ConfigureAwait(false); }
            finally { timeoutCts.Dispose(); completion.TrySetResult(); }
        }, CancellationToken.None);
        _logger.Info("job", $"Started async job '{jobId}' for tool '{tool.Name}'.");
        return _db.GetJob(jobId)!;
    }

    private async Task ExecuteJobAsync(string jobId, string sessionId, string workspace, string toolName, JsonObject args, CancellationTokenSource cts, CancellationTokenSource timeoutCts)
    {
        var gateAcquired = false;
        try
        {
            await _concurrencyGate.WaitAsync(cts.Token).ConfigureAwait(false);
            gateAcquired = true;

            _db.UpdateJobStatus(jobId, "running", eventMessage: $"Executing tool '{toolName}'.");

            var ctx = new ToolExecutionContext(
                SessionId: sessionId,
                JobId: jobId,
                ReportProgress: msg => _db.AppendJobEvent(jobId, "progress", msg),
                WorkspaceRoot: workspace);

            var result = await _registry.InvokeAsync(toolName, args, ctx, cts.Token).ConfigureAwait(false);
            var json = JsonSerializer.Serialize(result, AppConfig.JsonOptions);

            if (result.Success)
            {
                _db.UpdateJobStatus(jobId, "succeeded", resultJson: json, logExecId: result.ExecutionId, eventMessage: result.Summary);
            }
            else if (timeoutCts.IsCancellationRequested)
            {
                _db.UpdateJobStatus(jobId, "timed_out", resultJson: json, errorMessage: result.ErrorMessage, logExecId: result.ExecutionId, eventMessage: "Job timed out; not safe to assume a command had no side effects.");
            }
            else if (result.ErrorCode == "cancelled" || cts.IsCancellationRequested)
            {
                _db.UpdateJobStatus(jobId, "cancelled", resultJson: json, errorMessage: result.ErrorMessage, logExecId: result.ExecutionId, eventMessage: "Job cancelled.");
            }
            else
            {
                _db.UpdateJobStatus(jobId, "failed", resultJson: json, errorMessage: result.ErrorMessage, logExecId: result.ExecutionId, eventMessage: result.Summary);
            }
        }
        catch (OperationCanceledException)
        {
            var timedOut = timeoutCts.IsCancellationRequested;
            _db.UpdateJobStatus(jobId, timedOut ? "timed_out" : "cancelled",
                errorMessage: timedOut ? "Job timed out; outcome may require reconciliation." : "Job was cancelled.",
                eventMessage: timedOut ? "Job deadline reached." : "Job cancelled.");
        }
        catch (Exception ex)
        {
            _db.UpdateJobStatus(jobId, "failed", errorMessage: ex.Message, eventMessage: $"Unhandled error: {ex.Message}");
        }
        finally
        {
            if (gateAcquired)
            {
                try { _concurrencyGate.Release(); }
                catch (ObjectDisposedException) { /* shutdown racing worker unwind */ }
            }
            _active.TryRemove(jobId, out _);
            cts.Dispose();
        }
    }

    public bool CancelJob(string jobId)
    {
        if (_active.TryGetValue(jobId, out var running))
        {
            try { running.Cts.Cancel(); } catch { }
            _db.AppendJobEvent(jobId, "cancel_requested", "Cancellation signal sent to job.");
            _logger.Warn("job", $"Cancellation requested for job '{jobId}'.");
            return true;
        }
        return false;
    }

    public async Task ShutdownAsync(TimeSpan gracePeriod)
    {
        try { _hostCts.Cancel(); } catch { }
        var tasks = _active.Values.Select(j => j.Task).ToArray();
        if (tasks.Length > 0)
        {
            await Task.WhenAny(Task.WhenAll(tasks), Task.Delay(gracePeriod)).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        ShutdownAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
        _hostCts.Dispose();
        _concurrencyGate.Dispose();
    }
}
