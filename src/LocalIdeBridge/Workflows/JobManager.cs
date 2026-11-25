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
    private readonly object _admission = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _jobSignals = new();

    private static TaskCompletionSource<bool> NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void SignalChange(string jobId)
    {
        // Never recreate a signal after a completed job has been retired.
        while (_jobSignals.TryGetValue(jobId, out var previous))
        {
            if (!_jobSignals.TryUpdate(jobId, NewSignal(), previous)) continue;
            previous.TrySetResult(true);
            return;
        }
    }

    /// <summary>Waits without polling; caller must recheck durable state before and after registration.</summary>
    public async Task<bool> WaitForChangeAsync(string jobId, Func<bool> hasUpdate, int waitMs, CancellationToken ct)
    {
        if (!_jobSignals.TryGetValue(jobId, out var observed)) return hasUpdate();
        if (hasUpdate()) return true; // Covers notification before registration.
        if (waitMs <= 0) return false;
        try
        {
            await observed.Task.WaitAsync(TimeSpan.FromMilliseconds(Math.Clamp(waitMs, 1, 25_000)), ct)
                .ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException) { return hasUpdate(); }
    }

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

    public JobRecord StartToolJob(string toolName, JsonObject? arguments, int? timeoutSeconds = null,
        string? pinnedSessionId = null, string? pinnedWorkspace = null)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (timeoutSeconds is <= 0)
            throw new ArgumentException("Job timeoutSeconds must be positive when provided.");

        var tool = _registry.Find(toolName)
            ?? throw new ArgumentException($"Unknown tool '{toolName}'.");
        if (tool.Name.Equals("job_start", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Cannot recursively nest job_start inside job_start.");

        var jobId = $"job_{DateTimeOffset.UtcNow:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString("N")[..8]}";
        var sessionId = pinnedSessionId ?? _db.InvocationSessionId;
        var ws = pinnedWorkspace ?? _guard.ActiveWorkspace ?? "";
        var clonedArgs = arguments is null ? new JsonObject() : (JsonObject)arguments.DeepClone();
        var serializedArgs = clonedArgs.ToJsonString();
        if (System.Text.Encoding.UTF8.GetByteCount(serializedArgs) > 128 * 1024)
            throw new ArgumentException("Background job arguments exceed 128 KiB; use a bounded workspace file or a smaller payload.");

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
        try
        {
            lock (_admission)
            {
                // Include queued tasks, not just actively running subprocesses.
                // Admission happens BEFORE durable job creation and worker dispatch.
                var capacity = Math.Max(8, _config.Resources.MaxConcurrentJobs * 8);
                if (_active.Count >= capacity)
                    throw new InvalidOperationException($"queue_full: {capacity} background jobs are already admitted.");
                _db.CreateJob(jobId, sessionId, ws, tool.Name, serializedArgs, tool.IsIdempotent);
                _jobSignals[jobId] = NewSignal();
                _active[jobId] = new RunningJob(jobId, cts, completion.Task);
            }
        }
        catch
        {
            cts.Dispose();
            timeoutCts.Dispose();
            throw;
        }

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
            SignalChange(jobId);

            var ctx = new ToolExecutionContext(
                SessionId: sessionId,
                JobId: jobId,
                ReportProgress: msg =>
                {
                    _db.AppendJobEvent(jobId, "progress", msg);
                    SignalChange(jobId);
                },
                WorkspaceRoot: string.IsNullOrWhiteSpace(workspace) ? null : workspace);

            var result = await _registry.InvokeAsync(toolName, args, ctx, cts.Token).ConfigureAwait(false);
            var json = JsonSerializer.Serialize(result, AppConfig.JsonOptions);

            // A cancellation requested while a tool is finishing must take precedence
            // over a subprocess exit/error caused by termination. Keep timeout distinct.
            if (timeoutCts.IsCancellationRequested)
            {
                _db.UpdateJobStatus(jobId, "timed_out", resultJson: json, errorMessage: result.ErrorMessage, logExecId: result.ExecutionId, eventMessage: "Job deadline reached.");
            }
            else if (cts.IsCancellationRequested || result.ErrorCode == "cancelled")
            {
                _db.UpdateJobStatus(jobId, "cancelled", resultJson: json, errorMessage: result.ErrorMessage, logExecId: result.ExecutionId, eventMessage: "Job cancelled.");
            }
            else if (result.Success)
            {
                _db.UpdateJobStatus(jobId, "succeeded", resultJson: json, logExecId: result.ExecutionId, eventMessage: result.Summary);
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
            // Killing a child process can surface as IOException/InvalidOperationException
            // rather than OperationCanceledException. Preserve the requested outcome.
            var status = timeoutCts.IsCancellationRequested ? "timed_out"
                : cts.IsCancellationRequested ? "cancelled" : "failed";
            _db.UpdateJobStatus(jobId, status, errorMessage: ex.Message,
                eventMessage: status == "failed" ? $"Unhandled error: {ex.Message}" : $"Job {status} while unwinding: {ex.GetType().Name}.");
        }
        finally
        {
            if (_jobSignals.TryRemove(jobId, out var finalSignal))
                finalSignal.TrySetResult(true); // Wake waiters without retaining completed IDs.
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
            SignalChange(jobId);
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
