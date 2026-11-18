using System.Text.Json;
using LocalIdeBridge.Core;
using LocalIdeBridge.Tools;
using Microsoft.Data.Sqlite;

namespace LocalIdeBridge.Storage;

public sealed record SessionRecord(
    string Id,
    string Workspace,
    string Status,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    int ToolCallCount,
    int ErrorCount,
    string? SteeringNote);

public sealed record ToolCallRecord(
    long Id,
    string SessionId,
    string ToolName,
    string Category,
    string Workspace,
    bool Success,
    long DurationMs,
    string Summary,
    string? ErrorCode,
    DateTimeOffset CreatedAtUtc);

public sealed record JobRecord(
    string Id,
    string SessionId,
    string Workspace,
    string ToolName,
    string Status,
    bool IsIdempotent,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    string? ResultJson,
    string? ErrorMessage,
    string? LogExecId);

public sealed record JobEventRecord(
    long Id,
    string JobId,
    string EventType,
    string Message,
    DateTimeOffset CreatedAtUtc);

public sealed record EditJournalRecord(
    string EditId,
    string SessionId,
    string Workspace,
    string RelativePath,
    string Operation,
    string BeforeSha256,
    string AfterSha256,
    bool ExistedBefore,
    string? BackupFilePath,
    int LinesAdded,
    int LinesRemoved,
    bool Reverted,
    DateTimeOffset CreatedAtUtc);

public sealed record OperationLedgerRecord(
    string OperationId,
    string SessionId,
    string Workspace,
    string ToolName,
    string ArgumentsSha256,
    string Status,
    string? ResultJson,
    DateTimeOffset UpdatedAtUtc);

public sealed record GitCheckpointRecord(
    string Id,
    string SessionId,
    string Workspace,
    string Branch,
    string CommitSha,
    string Label,
    DateTimeOffset CreatedAtUtc);

public sealed record WorkflowRecord(
    string Id,
    string SessionId,
    string Workspace,
    string Name,
    string Status,
    int TotalSteps,
    int CompletedSteps,
    int FailedSteps,
    string StateJson,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

/// <summary>
/// Bounded-memory SQLite state store using WAL mode and strictly paginated queries.
/// Pooling is disabled so file handles release immediately on host shutdown.
/// </summary>
public sealed class StateDatabase : IDisposable
{
    private readonly PortablePaths _paths;
    private readonly CredentialStore _credentials;
    private readonly RetentionSettings _retention;
    private readonly string _connectionString;
    private readonly object _writeLock = new();
    private string _activeSessionId = "";
    // Invocation-scoped session attribution for commands launched by background jobs
    // and workflows while another client switches the host's active session.
    private readonly AsyncLocal<string?> _invocationSession = new();
    public string InvocationSessionId => _invocationSession.Value ?? ActiveSessionId;
    public IDisposable UseInvocationSession(string sessionId)
    {
        var previous = _invocationSession.Value;
        _invocationSession.Value = sessionId;
        return new InvocationSessionLease(_invocationSession, previous);
    }
    private sealed class InvocationSessionLease(AsyncLocal<string?> current, string? previous) : IDisposable
    {
        public void Dispose() => current.Value = previous;
    }
    private bool _disposed;

    public string ActiveSessionId
    {
        get { lock (_writeLock) return _activeSessionId; }
    }

    public StateDatabase(PortablePaths paths, CredentialStore credentials, RetentionSettings retention)
    {
        _paths = paths;
        _credentials = credentials;
        _retention = retention;
        Directory.CreateDirectory(paths.DataDir);

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = paths.StateDbFile,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        };
        _connectionString = builder.ToString();
    }

    private SqliteConnection OpenConnection()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var pragma = conn.CreateCommand();
        pragma.CommandText = """
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = FULL;
            PRAGMA cache_size = -2048;
            PRAGMA temp_store = FILE;
            PRAGMA foreign_keys = ON;
            PRAGMA busy_timeout = 5000;
            """;
        pragma.ExecuteNonQuery();
        return conn;
    }

    public int Initialize(string initialWorkspace, bool deferPruning = false)
    {
        lock (_writeLock)
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS sessions (
                    id TEXT PRIMARY KEY,
                    workspace TEXT NOT NULL,
                    status TEXT NOT NULL,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL,
                    tool_call_count INTEGER NOT NULL DEFAULT 0,
                    error_count INTEGER NOT NULL DEFAULT 0,
                    steering_note TEXT
                );
                CREATE INDEX IF NOT EXISTS idx_sessions_updated ON sessions(updated_at DESC);

                CREATE TABLE IF NOT EXISTS tool_calls (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    session_id TEXT NOT NULL,
                    tool_name TEXT NOT NULL,
                    category TEXT NOT NULL,
                    workspace TEXT NOT NULL,
                    success INTEGER NOT NULL,
                    duration_ms INTEGER NOT NULL,
                    summary TEXT NOT NULL,
                    error_code TEXT,
                    created_at TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_tool_calls_session ON tool_calls(session_id, id DESC);
                CREATE INDEX IF NOT EXISTS idx_tool_calls_recent ON tool_calls(id DESC);

                CREATE TABLE IF NOT EXISTS jobs (
                    id TEXT PRIMARY KEY,
                    session_id TEXT NOT NULL,
                    workspace TEXT NOT NULL,
                    tool_name TEXT NOT NULL,
                    args_json TEXT NOT NULL,
                    status TEXT NOT NULL,
                    is_idempotent INTEGER NOT NULL DEFAULT 0,
                    created_at TEXT NOT NULL,
                    started_at TEXT,
                    completed_at TEXT,
                    result_json TEXT,
                    error_message TEXT,
                    log_exec_id TEXT
                );
                CREATE INDEX IF NOT EXISTS idx_jobs_session ON jobs(session_id, created_at DESC);
                CREATE INDEX IF NOT EXISTS idx_jobs_status ON jobs(status);

                CREATE TABLE IF NOT EXISTS job_events (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    job_id TEXT NOT NULL,
                    event_type TEXT NOT NULL,
                    message TEXT NOT NULL,
                    created_at TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_job_events_job ON job_events(job_id, id ASC);

                CREATE TABLE IF NOT EXISTS edit_journal (
                    edit_id TEXT PRIMARY KEY,
                    session_id TEXT NOT NULL,
                    workspace TEXT NOT NULL,
                    relative_path TEXT NOT NULL,
                    operation TEXT NOT NULL,
                    before_sha256 TEXT NOT NULL,
                    after_sha256 TEXT NOT NULL,
                    existed_before INTEGER NOT NULL,
                    backup_file_path TEXT,
                    lines_added INTEGER NOT NULL,
                    lines_removed INTEGER NOT NULL,
                    reverted INTEGER NOT NULL DEFAULT 0,
                    created_at TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_edit_journal_session ON edit_journal(session_id, created_at DESC);

                CREATE TABLE IF NOT EXISTS edit_reversal_links (
                    original_edit_id TEXT PRIMARY KEY,
                    inverse_edit_id TEXT NOT NULL UNIQUE
                );
                CREATE TABLE IF NOT EXISTS git_checkpoints (
                    id TEXT PRIMARY KEY,
                    session_id TEXT NOT NULL,
                    workspace TEXT NOT NULL,
                    branch TEXT NOT NULL,
                    commit_sha TEXT NOT NULL,
                    label TEXT NOT NULL,
                    created_at TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_git_checkpoints_session ON git_checkpoints(session_id, created_at DESC);

                CREATE TABLE IF NOT EXISTS workflow_runs (
                    id TEXT PRIMARY KEY,
                    session_id TEXT NOT NULL,
                    workspace TEXT NOT NULL,
                    name TEXT NOT NULL,
                    status TEXT NOT NULL,
                    total_steps INTEGER NOT NULL,
                    completed_steps INTEGER NOT NULL,
                    failed_steps INTEGER NOT NULL,
                    state_json TEXT NOT NULL,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_workflows_session ON workflow_runs(session_id, created_at DESC);

                CREATE TABLE IF NOT EXISTS session_change_summaries (
                    session_id TEXT PRIMARY KEY,
                    summary_json TEXT NOT NULL,
                    observed_at TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS operation_ledger (
                    operation_id TEXT PRIMARY KEY,
                    session_id TEXT NOT NULL,
                    workspace TEXT NOT NULL,
                    tool_name TEXT NOT NULL,
                    arguments_sha256 TEXT NOT NULL,
                    status TEXT NOT NULL,
                    result_json TEXT,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_operations_session ON operation_ledger(session_id, updated_at DESC);

                CREATE TABLE IF NOT EXISTS log_index (
                    exec_id TEXT PRIMARY KEY,
                    session_id TEXT NOT NULL,
                    file_path TEXT NOT NULL,
                    command_category TEXT NOT NULL,
                    total_lines INTEGER NOT NULL DEFAULT 0,
                    total_bytes INTEGER NOT NULL DEFAULT 0,
                    truncated INTEGER NOT NULL DEFAULT 0,
                    created_at TEXT NOT NULL
                );
                """;
            cmd.ExecuteNonQuery();

            var recovered = RecoverInterruptedStateLocked(conn);
            _activeSessionId = CreateSessionInternalLocked(conn, initialWorkspace);
            if (!deferPruning) PruneOldSessionsLocked(conn);
            return recovered;
        }
    }

    /// <summary>
    /// Marks jobs and workflows left in running/queued states from an abnormal exit
    /// as 'interrupted_restart' without automatically replaying non-idempotent commands.
    /// </summary>
    private static int RecoverInterruptedStateLocked(SqliteConnection conn)
    {
        var now = DateTimeOffset.UtcNow.ToString("O");
        var affectedIds = new List<string>();
        using (var find = conn.CreateCommand())
        {
            find.CommandText = "SELECT id FROM jobs WHERE status IN ('queued', 'running');";
            using var r = find.ExecuteReader();
            while (r.Read()) affectedIds.Add(r.GetString(0));
        }

        if (affectedIds.Count > 0)
        {
            using var upd = conn.CreateCommand();
            upd.CommandText = """
                UPDATE jobs
                SET status = 'interrupted_restart',
                    completed_at = $now,
                    error_message = 'Host restarted before completion; non-idempotent command was not replayed automatically.'
                WHERE status IN ('queued', 'running');
                """;
            upd.Parameters.AddWithValue("$now", now);
            upd.ExecuteNonQuery();

            foreach (var jobId in affectedIds)
            {
                using var evt = conn.CreateCommand();
                evt.CommandText = """
                    INSERT INTO job_events(job_id, event_type, message, created_at)
                    VALUES ($j, 'interrupted_restart', 'Recovered after host restart; inspect workspace state before retrying.', $now);
                    """;
                evt.Parameters.AddWithValue("$j", jobId);
                evt.Parameters.AddWithValue("$now", now);
                evt.ExecuteNonQuery();
            }
        }

        using (var wf = conn.CreateCommand())
        {
            wf.CommandText = """
                UPDATE workflow_runs
                SET status = 'interrupted_restart', updated_at = $now
                WHERE status IN ('queued', 'running');
                """;
            wf.Parameters.AddWithValue("$now", now);
            affectedIds.AddRange(Enumerable.Repeat("wf", wf.ExecuteNonQuery()));
        }

        // Ambiguous execution completion is NEVER an invitation to repeat side effects.
        using (var ops = conn.CreateCommand())
        {
            ops.CommandText = "UPDATE operation_ledger SET status = 'interrupted_unknown', updated_at = $now WHERE status = 'running';";
            ops.Parameters.AddWithValue("$now", now);
            ops.ExecuteNonQuery();
        }

        using (var sess = conn.CreateCommand())
        {
            sess.CommandText = "UPDATE sessions SET status = 'closed', updated_at = $now WHERE status = 'active';";
            sess.Parameters.AddWithValue("$now", now);
            sess.ExecuteNonQuery();
        }

        return affectedIds.Count;
    }

    public SessionRecord CreateAndActivateSession(string workspace)
    {
        lock (_writeLock)
        {
            using var conn = OpenConnection();
            var now = DateTimeOffset.UtcNow.ToString("O");
            using (var close = conn.CreateCommand())
            {
                close.CommandText = "UPDATE sessions SET status = 'closed', updated_at = $now WHERE id = $id;";
                close.Parameters.AddWithValue("$now", now);
                close.Parameters.AddWithValue("$id", _activeSessionId);
                close.ExecuteNonQuery();
            }
            _activeSessionId = CreateSessionInternalLocked(conn, workspace);
            PruneOldSessionsLocked(conn);
            return GetSessionLocked(conn, _activeSessionId)!;
        }
    }

    public SessionRecord SwitchSession(string sessionId)
    {
        lock (_writeLock)
        {
            using var conn = OpenConnection();
            var target = GetSessionLocked(conn, sessionId)
                ?? throw new KeyNotFoundException($"Session '{sessionId}' not found.");
            var now = DateTimeOffset.UtcNow.ToString("O");
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = """
                    UPDATE sessions SET status = 'closed', updated_at = $now WHERE id = $old AND id <> $new;
                    UPDATE sessions SET status = 'active', updated_at = $now WHERE id = $new;
                    """;
                cmd.Parameters.AddWithValue("$now", now);
                cmd.Parameters.AddWithValue("$old", _activeSessionId);
                cmd.Parameters.AddWithValue("$new", target.Id);
                cmd.ExecuteNonQuery();
            }
            _activeSessionId = target.Id;
            return GetSessionLocked(conn, target.Id)!;
        }
    }

    public void SetSteeringNote(string? note)
    {
        lock (_writeLock)
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE sessions SET steering_note = $note, updated_at = $now WHERE id = $id;";
            cmd.Parameters.AddWithValue("$note", string.IsNullOrWhiteSpace(note) ? DBNull.Value : _credentials.Redact(note.Trim()));
            cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("$id", _activeSessionId);
            cmd.ExecuteNonQuery();
        }
    }

    private string CreateSessionInternalLocked(SqliteConnection conn, string workspace)
    {
        var id = $"sess_{DateTimeOffset.UtcNow:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString("N")[..8]}";
        var now = DateTimeOffset.UtcNow.ToString("O");
        _paths.GetSessionDir(id);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO sessions(id, workspace, status, created_at, updated_at, tool_call_count, error_count)
            VALUES ($id, $ws, 'active', $now, $now, 0, 0);
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$ws", workspace);
        cmd.Parameters.AddWithValue("$now", now);
        cmd.ExecuteNonQuery();
        return id;
    }

    public SessionRecord? GetSession(string sessionId)
    {
        using var conn = OpenConnection();
        return GetSessionLocked(conn, sessionId);
    }

    private static SessionRecord? GetSessionLocked(SqliteConnection conn, string sessionId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, workspace, status, created_at, updated_at, tool_call_count, error_count, steering_note
            FROM sessions WHERE id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", sessionId);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new SessionRecord(
            r.GetString(0),
            r.GetString(1),
            r.GetString(2),
            DateTimeOffset.Parse(r.GetString(3)),
            DateTimeOffset.Parse(r.GetString(4)),
            r.GetInt32(5),
            r.GetInt32(6),
            r.IsDBNull(7) ? null : r.GetString(7));
    }

    public IReadOnlyList<SessionRecord> ListSessions(int limit = 20, int offset = 0)
    {
        limit = Math.Clamp(limit, 1, 100);
        offset = Math.Max(0, offset);
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, workspace, status, created_at, updated_at, tool_call_count, error_count, steering_note
            FROM sessions ORDER BY updated_at DESC LIMIT $limit OFFSET $offset;
            """;
        cmd.Parameters.AddWithValue("$limit", limit);
        cmd.Parameters.AddWithValue("$offset", offset);
        var list = new List<SessionRecord>(limit);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new SessionRecord(
                r.GetString(0), r.GetString(1), r.GetString(2),
                DateTimeOffset.Parse(r.GetString(3)), DateTimeOffset.Parse(r.GetString(4)),
                r.GetInt32(5), r.GetInt32(6), r.IsDBNull(7) ? null : r.GetString(7)));
        }
        return list;
    }

    public long RecordToolCall(string toolName, string category, string workspace, bool success, long durationMs, string summary, string? errorCode = null, string? sessionId = null)
    {
        var safeSummary = _credentials.Redact(summary);
        if (safeSummary.Length > 500) safeSummary = safeSummary[..500] + "...";
        var now = DateTimeOffset.UtcNow.ToString("O");

        lock (_writeLock)
        {
            var sid = sessionId ?? _activeSessionId;
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO tool_calls(session_id, tool_name, category, workspace, success, duration_ms, summary, error_code, created_at)
                VALUES ($sid, $tool, $cat, $ws, $ok, $dur, $sum, $err, $now);
                UPDATE sessions
                SET tool_call_count = tool_call_count + 1,
                    error_count = error_count + $errInc,
                    workspace = CASE WHEN $ws <> '' THEN $ws ELSE workspace END,
                    updated_at = $now
                WHERE id = $sid;
                """;
            cmd.Parameters.AddWithValue("$sid", sid);
            cmd.Parameters.AddWithValue("$tool", toolName);
            cmd.Parameters.AddWithValue("$cat", category);
            cmd.Parameters.AddWithValue("$ws", workspace);
            cmd.Parameters.AddWithValue("$ok", success ? 1 : 0);
            cmd.Parameters.AddWithValue("$dur", durationMs);
            cmd.Parameters.AddWithValue("$sum", safeSummary);
            cmd.Parameters.AddWithValue("$err", errorCode is null ? DBNull.Value : errorCode);
            cmd.Parameters.AddWithValue("$errInc", success ? 0 : 1);
            cmd.Parameters.AddWithValue("$now", now);
            cmd.ExecuteNonQuery();
            using var identity = conn.CreateCommand();
            identity.CommandText = "SELECT last_insert_rowid();";
            return Convert.ToInt64(identity.ExecuteScalar());
        }
    }

    public IReadOnlyList<ToolCallRecord> ListToolCalls(string? sessionId = null, int limit = 25, int offset = 0, bool errorsOnly = false)
    {
        limit = Math.Clamp(limit, 1, 100);
        offset = Math.Max(0, offset);
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        var where = new List<string>();
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            where.Add("session_id = $sid");
            cmd.Parameters.AddWithValue("$sid", sessionId);
        }
        if (errorsOnly) where.Add("success = 0");
        var whereSql = where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : "";

        cmd.CommandText = $"""
            SELECT id, session_id, tool_name, category, workspace, success, duration_ms, summary, error_code, created_at
            FROM tool_calls {whereSql} ORDER BY id DESC LIMIT $limit OFFSET $offset;
            """;
        cmd.Parameters.AddWithValue("$limit", limit);
        cmd.Parameters.AddWithValue("$offset", offset);

        var list = new List<ToolCallRecord>(limit);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new ToolCallRecord(
                r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4),
                r.GetInt32(5) == 1, r.GetInt64(6), r.GetString(7),
                r.IsDBNull(8) ? null : r.GetString(8), DateTimeOffset.Parse(r.GetString(9))));
        }
        return list;
    }

    /// <summary>Atomically reserve a client-supplied operation ID before a side effect.
    /// Retries never re-enter the wrapped tool, even if prior execution crashed.
    /// The first response contains Created=true; all others return original state.
    /// </summary>
    public (OperationLedgerRecord Record, bool Created) TryReserveOperation(
        string id, string sessionId, string workspace, string toolName, string argumentsSha256)
    {
        var now = DateTimeOffset.UtcNow.ToString("O");
        lock (_writeLock)
        {
            using var conn = OpenConnection();
            using var insert = conn.CreateCommand();
            insert.CommandText = """
                INSERT OR IGNORE INTO operation_ledger
                    (operation_id, session_id, workspace, tool_name, arguments_sha256, status, created_at, updated_at)
                VALUES ($id, $sid, $ws, $tool, $hash, 'running', $now, $now);
                """;
            insert.Parameters.AddWithValue("$id", id);
            insert.Parameters.AddWithValue("$sid", sessionId);
            insert.Parameters.AddWithValue("$ws", workspace);
            insert.Parameters.AddWithValue("$tool", toolName);
            insert.Parameters.AddWithValue("$hash", argumentsSha256);
            insert.Parameters.AddWithValue("$now", now);
            var created = insert.ExecuteNonQuery() > 0;
            using var select = conn.CreateCommand();
            select.CommandText = """
                SELECT operation_id, session_id, workspace, tool_name, arguments_sha256,
                    status, result_json, updated_at FROM operation_ledger WHERE operation_id = $id;
                """;
            select.Parameters.AddWithValue("$id", id);
            using var r = select.ExecuteReader();
            if (!r.Read()) throw new IOException("Operation reservation vanished; refusing to execute.");
            return (ReadOperation(r), created);
        }
    }

    public OperationLedgerRecord? GetOperation(string operationId)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT operation_id, session_id, workspace, tool_name, arguments_sha256,
                status, result_json, updated_at FROM operation_ledger WHERE operation_id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", operationId);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadOperation(r) : null;
    }

    private static OperationLedgerRecord ReadOperation(SqliteDataReader r) => new(
        r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4),
        r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6), DateTimeOffset.Parse(r.GetString(7)));

    public void CompleteOperation(string operationId, ToolResult result)
    {
        // Only persist bounded summaries; a large output belongs to the disk-backed
        // command log/job result store. Do not create unbounded state rows.
        var payload = JsonSerializer.Serialize(result, AppConfig.JsonOptions);
        if (payload.Length > 64_000)
            payload = JsonSerializer.Serialize(new ToolResult(result.Success, result.Summary,
                Data: new { fullResultStored = false, executionId = result.ExecutionId },
                ErrorCode: result.ErrorCode, ErrorMessage: result.ErrorMessage,
                ExecutionId: result.ExecutionId), AppConfig.JsonOptions);
        lock (_writeLock)
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE operation_ledger SET status = $st, result_json = $json, updated_at = $now
                WHERE operation_id = $id AND status = 'running';
                """;
            cmd.Parameters.AddWithValue("$id", operationId);
            cmd.Parameters.AddWithValue("$st", result.Success ? "succeeded" : "failed");
            cmd.Parameters.AddWithValue("$json", _credentials.Redact(payload));
            cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            if (cmd.ExecuteNonQuery() != 1)
                throw new IOException("Operation completion could not be persisted. Inspect existing outcome before retrying.");
        }
    }

    public void CreateJob(string jobId, string sessionId, string workspace, string toolName, string argsJson, bool isIdempotent)
    {
        var now = DateTimeOffset.UtcNow.ToString("O");
        lock (_writeLock)
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO jobs(id, session_id, workspace, tool_name, args_json, status, is_idempotent, created_at)
                VALUES ($id, $sid, $ws, $tool, $args, 'queued', $idem, $now);
                INSERT INTO job_events(job_id, event_type, message, created_at)
                VALUES ($id, 'queued', $msg, $now);
                """;
            cmd.Parameters.AddWithValue("$id", jobId);
            cmd.Parameters.AddWithValue("$sid", sessionId);
            cmd.Parameters.AddWithValue("$ws", workspace);
            cmd.Parameters.AddWithValue("$tool", toolName);
            cmd.Parameters.AddWithValue("$args", _credentials.Redact(argsJson));
            cmd.Parameters.AddWithValue("$idem", isIdempotent ? 1 : 0);
            cmd.Parameters.AddWithValue("$msg", $"Queued tool '{toolName}'.");
            cmd.Parameters.AddWithValue("$now", now);
            cmd.ExecuteNonQuery();
        }
    }

    public void UpdateJobStatus(string jobId, string status, string? resultJson = null, string? errorMessage = null, string? logExecId = null, string? eventMessage = null)
    {
        var now = DateTimeOffset.UtcNow.ToString("O");
        var safeResult = resultJson is null ? null : _credentials.Redact(resultJson);
        if (safeResult is { Length: > 64_000 })
        {
            // Store complete JSON on disk and return a structured reference, never an
            // invalid half-document which masquerades as a successful null result.
            var resultDir = Path.Combine(_paths.DataDir, "job-results");
            Directory.CreateDirectory(resultDir);
            var resultFile = Path.Combine(resultDir, PortablePaths.SanitizeId(jobId) + ".json");
            PortablePaths.AtomicWriteAllText(resultFile, safeResult);
            safeResult = JsonSerializer.Serialize(new
            {
                spilled = true,
                jobId,
                resultChars = safeResult.Length,
                sha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(safeResult))),
                retrieveWith = "job_result_slice"
            });
        }
        var safeErr = errorMessage is null ? null : _credentials.Redact(errorMessage);

        lock (_writeLock)
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            var setStarted = status == "running" ? ", started_at = COALESCE(started_at, $now)" : "";
            var setCompleted = status is "succeeded" or "failed" or "cancelled" or "timed_out" or "interrupted_restart" ? ", completed_at = $now" : "";
            cmd.CommandText = $"""
                UPDATE jobs
                SET status = $st {setStarted} {setCompleted},
                    result_json = COALESCE($res, result_json),
                    error_message = COALESCE($err, error_message),
                    log_exec_id = COALESCE($log, log_exec_id)
                WHERE id = $id;
                """;
            cmd.Parameters.AddWithValue("$st", status);
            cmd.Parameters.AddWithValue("$now", now);
            cmd.Parameters.AddWithValue("$res", safeResult is null ? DBNull.Value : safeResult);
            cmd.Parameters.AddWithValue("$err", safeErr is null ? DBNull.Value : safeErr);
            cmd.Parameters.AddWithValue("$log", logExecId is null ? DBNull.Value : logExecId);
            cmd.Parameters.AddWithValue("$id", jobId);
            cmd.ExecuteNonQuery();

            if (!string.IsNullOrWhiteSpace(eventMessage))
            {
                using var evt = conn.CreateCommand();
                evt.CommandText = "INSERT INTO job_events(job_id, event_type, message, created_at) VALUES ($id, $st, $msg, $now);";
                evt.Parameters.AddWithValue("$id", jobId);
                evt.Parameters.AddWithValue("$st", status);
                evt.Parameters.AddWithValue("$msg", _credentials.Redact(eventMessage));
                evt.Parameters.AddWithValue("$now", now);
                evt.ExecuteNonQuery();
            }
        }
    }

    public void AppendJobEvent(string jobId, string eventType, string message)
    {
        var safeMsg = _credentials.Redact(message);
        if (safeMsg.Length > 1000) safeMsg = safeMsg[..1000] + "...";
        lock (_writeLock)
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO job_events(job_id, event_type, message, created_at) VALUES ($id, $type, $msg, $now);";
            cmd.Parameters.AddWithValue("$id", jobId);
            cmd.Parameters.AddWithValue("$type", eventType);
            cmd.Parameters.AddWithValue("$msg", safeMsg);
            cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            cmd.ExecuteNonQuery();
        }
    }

    public (string Content, int Offset, int TotalChars, bool HasMore) ReadJobResultSlice(string jobId, int offset, int limit)
    {
        if (GetJob(jobId) is null) throw new KeyNotFoundException($"Job '{jobId}' not found.");
        var path = Path.Combine(_paths.DataDir, "job-results", PortablePaths.SanitizeId(jobId) + ".json");
        if (!File.Exists(path)) throw new FileNotFoundException("No spilled result exists for this job.");
        offset = Math.Max(0, offset);
        limit = Math.Clamp(limit, 1, 32_000);
        // File is a complete result, possibly large; seek with StreamReader character reads.
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 8192);
        using var reader = new StreamReader(fs, System.Text.Encoding.UTF8);
        var skip = new char[4096];
        var remaining = offset;
        while (remaining > 0)
        {
            var count = reader.Read(skip, 0, Math.Min(skip.Length, remaining));
            if (count == 0) return ("", offset, offset - remaining, false);
            remaining -= count;
        }
        var chars = new char[limit + 1];
        var read = 0;
        while (read < chars.Length)
        {
            var count = reader.Read(chars, read, chars.Length - read);
            if (count == 0) break;
            read += count;
        }
        return (new string(chars, 0, Math.Min(limit, read)), offset, -1, read > limit);
    }

    public JobRecord? GetJob(string jobId)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, session_id, workspace, tool_name, status, is_idempotent, created_at, started_at, completed_at, result_json, error_message, log_exec_id
            FROM jobs WHERE id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", jobId);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadJob(r) : null;
    }

    public IReadOnlyList<JobRecord> ListJobs(string? sessionId = null, string? status = null, int limit = 25, int offset = 0)
    {
        limit = Math.Clamp(limit, 1, 100);
        offset = Math.Max(0, offset);
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        var where = new List<string>();
        if (!string.IsNullOrWhiteSpace(sessionId)) { where.Add("session_id = $sid"); cmd.Parameters.AddWithValue("$sid", sessionId); }
        if (!string.IsNullOrWhiteSpace(status)) { where.Add("status = $st"); cmd.Parameters.AddWithValue("$st", status); }
        var whereSql = where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : "";

        cmd.CommandText = $"""
            SELECT id, session_id, workspace, tool_name, status, is_idempotent, created_at, started_at, completed_at, result_json, error_message, log_exec_id
            FROM jobs {whereSql} ORDER BY created_at DESC LIMIT $limit OFFSET $offset;
            """;
        cmd.Parameters.AddWithValue("$limit", limit);
        cmd.Parameters.AddWithValue("$offset", offset);
        var list = new List<JobRecord>(limit);
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(ReadJob(r));
        return list;
    }

    public IReadOnlyList<JobEventRecord> ListJobEvents(string jobId, long afterEventId = 0, int limit = 100)
    {
        limit = Math.Clamp(limit, 1, 200);
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, job_id, event_type, message, created_at
            FROM job_events WHERE job_id = $id AND id > $after ORDER BY id ASC LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$id", jobId);
        cmd.Parameters.AddWithValue("$after", afterEventId);
        cmd.Parameters.AddWithValue("$limit", limit);
        var list = new List<JobEventRecord>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new JobEventRecord(
                r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), DateTimeOffset.Parse(r.GetString(4))));
        }
        return list;
    }

    private static JobRecord ReadJob(SqliteDataReader r) => new(
        r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4),
        r.GetInt32(5) == 1,
        DateTimeOffset.Parse(r.GetString(6)),
        r.IsDBNull(7) ? null : DateTimeOffset.Parse(r.GetString(7)),
        r.IsDBNull(8) ? null : DateTimeOffset.Parse(r.GetString(8)),
        r.IsDBNull(9) ? null : r.GetString(9),
        r.IsDBNull(10) ? null : r.GetString(10),
        r.IsDBNull(11) ? null : r.GetString(11));

    public void RecordEdit(EditJournalRecord edit)
    {
        lock (_writeLock)
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO edit_journal(edit_id, session_id, workspace, relative_path, operation, before_sha256, after_sha256, existed_before, backup_file_path, lines_added, lines_removed, reverted, created_at)
                VALUES ($id, $sid, $ws, $path, $op, $before, $after, $existed, $backup, $add, $rem, 0, $now);
                """;
            cmd.Parameters.AddWithValue("$id", edit.EditId);
            cmd.Parameters.AddWithValue("$sid", edit.SessionId);
            cmd.Parameters.AddWithValue("$ws", edit.Workspace);
            cmd.Parameters.AddWithValue("$path", edit.RelativePath);
            cmd.Parameters.AddWithValue("$op", edit.Operation);
            cmd.Parameters.AddWithValue("$before", edit.BeforeSha256);
            cmd.Parameters.AddWithValue("$after", edit.AfterSha256);
            cmd.Parameters.AddWithValue("$existed", edit.ExistedBefore ? 1 : 0);
            cmd.Parameters.AddWithValue("$backup", edit.BackupFilePath is null ? DBNull.Value : edit.BackupFilePath);
            cmd.Parameters.AddWithValue("$add", edit.LinesAdded);
            cmd.Parameters.AddWithValue("$rem", edit.LinesRemoved);
            cmd.Parameters.AddWithValue("$now", edit.CreatedAtUtc.ToString("O"));
            cmd.ExecuteNonQuery();

            // Bound edit journal backups to configured max entries.
            using var prune = conn.CreateCommand();
            prune.CommandText = """
                SELECT edit_id, backup_file_path FROM edit_journal
                WHERE reverted = 1
                ORDER BY rowid DESC LIMIT -1 OFFSET $max;
                """;
            prune.Parameters.AddWithValue("$max", _retention.EditJournalMaxEntries);
            var old = new List<(string Id, string? Backup)>();
            using (var r = prune.ExecuteReader())
            {
                while (r.Read()) old.Add((r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1)));
            }
            foreach (var item in old)
            {
                if (!string.IsNullOrEmpty(item.Backup) && File.Exists(item.Backup))
                {
                    try { File.Delete(item.Backup); } catch { }
                }
                using var del = conn.CreateCommand();
                del.CommandText = "DELETE FROM edit_journal WHERE edit_id = $id;";
                del.Parameters.AddWithValue("$id", item.Id);
                del.ExecuteNonQuery();
            }
        }
    }

    /// <summary>
    /// Inverse record + original reverted flag are one SQLite transaction.
    /// Filesystem recovery still relies on the durable edit manifest.
    /// </summary>
    public void RecordEditAndRevert(EditJournalRecord inverse, string originalEditId)
    {
        lock (_writeLock)
        {
            using var conn = OpenConnection();
            using var tx = conn.BeginTransaction();
            using (var insert = conn.CreateCommand())
            {
                insert.Transaction = tx;
                insert.CommandText = """
                    INSERT INTO edit_journal(edit_id,session_id,workspace,relative_path,operation,
                        before_sha256,after_sha256,existed_before,backup_file_path,lines_added,
                        lines_removed,reverted,created_at)
                    VALUES($id,$sid,$ws,$path,$op,$before,$after,$existed,$backup,$add,$rem,0,$now);
                    """;
                insert.Parameters.AddWithValue("$id", inverse.EditId);
                insert.Parameters.AddWithValue("$sid", inverse.SessionId);
                insert.Parameters.AddWithValue("$ws", inverse.Workspace);
                insert.Parameters.AddWithValue("$path", inverse.RelativePath);
                insert.Parameters.AddWithValue("$op", inverse.Operation);
                insert.Parameters.AddWithValue("$before", inverse.BeforeSha256);
                insert.Parameters.AddWithValue("$after", inverse.AfterSha256);
                insert.Parameters.AddWithValue("$existed", inverse.ExistedBefore ? 1 : 0);
                insert.Parameters.AddWithValue("$backup", inverse.BackupFilePath is null ? DBNull.Value : inverse.BackupFilePath);
                insert.Parameters.AddWithValue("$add", inverse.LinesAdded);
                insert.Parameters.AddWithValue("$rem", inverse.LinesRemoved);
                insert.Parameters.AddWithValue("$now", inverse.CreatedAtUtc.ToString("O"));
                insert.ExecuteNonQuery();
            }
            using (var mark = conn.CreateCommand())
            {
                mark.Transaction = tx;
                mark.CommandText = "UPDATE edit_journal SET reverted=1 WHERE edit_id=$id AND reverted=0;";
                mark.Parameters.AddWithValue("$id", originalEditId);
                if (mark.ExecuteNonQuery() != 1)
                    throw new InvalidOperationException($"Original edit '{originalEditId}' already reverted or unavailable.");
            }
            using (var link = conn.CreateCommand())
            {
                link.Transaction = tx;
                link.CommandText = "INSERT INTO edit_reversal_links(original_edit_id,inverse_edit_id) VALUES($original,$inverse);";
                link.Parameters.AddWithValue("$original", originalEditId);
                link.Parameters.AddWithValue("$inverse", inverse.EditId);
                link.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    public void ReconcileRolledBackInverse(string inverseEditId, string originalEditId)
    {
        lock (_writeLock)
        {
            using var conn = OpenConnection();
            using var tx = conn.BeginTransaction();
            foreach (var statement in new[]
            {
                "UPDATE edit_journal SET reverted=1 WHERE edit_id=$inverse;",
                "UPDATE edit_journal SET reverted=0 WHERE edit_id=$original;",
                "DELETE FROM edit_reversal_links WHERE original_edit_id=$original AND inverse_edit_id=$inverse;"
            })
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = statement;
                cmd.Parameters.AddWithValue("$original", originalEditId);
                cmd.Parameters.AddWithValue("$inverse", inverseEditId);
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    public EditJournalRecord? GetEdit(string editId)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT edit_id, session_id, workspace, relative_path, operation, before_sha256, after_sha256, existed_before, backup_file_path, lines_added, lines_removed, reverted, created_at
            FROM edit_journal WHERE edit_id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", editId);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new EditJournalRecord(
            r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4),
            r.GetString(5), r.GetString(6), r.GetInt32(7) == 1,
            r.IsDBNull(8) ? null : r.GetString(8),
            r.GetInt32(9), r.GetInt32(10), r.GetInt32(11) == 1,
            DateTimeOffset.Parse(r.GetString(12)));
    }

    public void MarkEditReverted(string editId)
    {
        lock (_writeLock)
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE edit_journal SET reverted = 1 WHERE edit_id = $id;";
            cmd.Parameters.AddWithValue("$id", editId);
            cmd.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<EditJournalRecord> ListRecentEdits(string? sessionId = null, int limit = 20)
    {
        limit = Math.Clamp(limit, 1, 100);
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        var where = string.IsNullOrWhiteSpace(sessionId) ? "" : "WHERE session_id = $sid";
        if (!string.IsNullOrWhiteSpace(sessionId)) cmd.Parameters.AddWithValue("$sid", sessionId);
        cmd.CommandText = $"""
            SELECT edit_id, session_id, workspace, relative_path, operation, before_sha256, after_sha256, existed_before, backup_file_path, lines_added, lines_removed, reverted, created_at
            FROM edit_journal {where} ORDER BY created_at DESC LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$limit", limit);
        var list = new List<EditJournalRecord>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new EditJournalRecord(
                r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4),
                r.GetString(5), r.GetString(6), r.GetInt32(7) == 1,
                r.IsDBNull(8) ? null : r.GetString(8),
                r.GetInt32(9), r.GetInt32(10), r.GetInt32(11) == 1,
                DateTimeOffset.Parse(r.GetString(12))));
        }
        return list;
    }

    /// <summary>Finite edit history for verified first-touch session diffs (oldest first).</summary>
    public IReadOnlyList<EditJournalRecord> ListSessionEdits(string sessionId, int maxRows = 2500)
    {
        maxRows = Math.Clamp(maxRows, 1, 5001);
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT edit_id, session_id, workspace, relative_path, operation, before_sha256, after_sha256,
                   existed_before, backup_file_path, lines_added, lines_removed, reverted, created_at
            FROM edit_journal WHERE session_id = $sid ORDER BY rowid ASC LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$sid", sessionId);
        cmd.Parameters.AddWithValue("$limit", maxRows);
        var entries = new List<EditJournalRecord>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            entries.Add(new EditJournalRecord(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
                reader.GetString(5), reader.GetString(6), reader.GetInt32(7) == 1,
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.GetInt32(9), reader.GetInt32(10), reader.GetInt32(11) == 1,
                DateTimeOffset.Parse(reader.GetString(12))));
        return entries;
    }

    public void SaveSessionChangeSummary(SessionChangeSummary summary)
    {
        var json = JsonSerializer.Serialize(summary, AppConfig.JsonOptions);
        lock (_writeLock)
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO session_change_summaries(session_id, summary_json, observed_at)
                VALUES($sid, $json, $now)
                ON CONFLICT(session_id) DO UPDATE
                SET summary_json = excluded.summary_json, observed_at = excluded.observed_at;
                """;
            cmd.Parameters.AddWithValue("$sid", summary.SessionId);
            cmd.Parameters.AddWithValue("$json", json);
            cmd.Parameters.AddWithValue("$now", summary.ObservedAtUtc.ToString("O"));
            cmd.ExecuteNonQuery();
        }
    }

    public SessionChangeSummary? GetSessionChangeSummary(string sessionId)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT summary_json FROM session_change_summaries WHERE session_id = $sid;";
        cmd.Parameters.AddWithValue("$sid", sessionId);
        var json = cmd.ExecuteScalar() as string;
        return json is null ? null : JsonSerializer.Deserialize<SessionChangeSummary>(json, AppConfig.JsonOptions);
    }

    public void RecordGitCheckpoint(GitCheckpointRecord cp)
    {
        lock (_writeLock)
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO git_checkpoints(id, session_id, workspace, branch, commit_sha, label, created_at)
                VALUES ($id, $sid, $ws, $br, $sha, $lbl, $now);
                """;
            cmd.Parameters.AddWithValue("$id", cp.Id);
            cmd.Parameters.AddWithValue("$sid", cp.SessionId);
            cmd.Parameters.AddWithValue("$ws", cp.Workspace);
            cmd.Parameters.AddWithValue("$br", cp.Branch);
            cmd.Parameters.AddWithValue("$sha", cp.CommitSha);
            cmd.Parameters.AddWithValue("$lbl", _credentials.Redact(cp.Label));
            cmd.Parameters.AddWithValue("$now", cp.CreatedAtUtc.ToString("O"));
            cmd.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<GitCheckpointRecord> ListGitCheckpoints(string? sessionId = null, int limit = 20)
    {
        limit = Math.Clamp(limit, 1, 100);
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        var where = string.IsNullOrWhiteSpace(sessionId) ? "" : "WHERE session_id = $sid";
        if (!string.IsNullOrWhiteSpace(sessionId)) cmd.Parameters.AddWithValue("$sid", sessionId);
        cmd.CommandText = $"SELECT id, session_id, workspace, branch, commit_sha, label, created_at FROM git_checkpoints {where} ORDER BY created_at DESC LIMIT $limit;";
        cmd.Parameters.AddWithValue("$limit", limit);
        var list = new List<GitCheckpointRecord>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new GitCheckpointRecord(
                r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), DateTimeOffset.Parse(r.GetString(6))));
        }
        return list;
    }

    public IReadOnlyList<GitCheckpointRecord> ListWorkspaceGitCheckpoints(string workspace, int limit = 10)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, session_id, workspace, branch, commit_sha, label, created_at
            FROM git_checkpoints WHERE workspace = $ws
            ORDER BY created_at DESC LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$ws", workspace);
        cmd.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 100));
        var list = new List<GitCheckpointRecord>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new GitCheckpointRecord(reader.GetString(0), reader.GetString(1),
                reader.GetString(2), reader.GetString(3), reader.GetString(4),
                reader.GetString(5), DateTimeOffset.Parse(reader.GetString(6))));
        }
        return list;
    }

    public void SaveWorkflow(WorkflowRecord wf)
    {
        var safeJson = _credentials.Redact(wf.StateJson);
        lock (_writeLock)
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO workflow_runs(id, session_id, workspace, name, status, total_steps, completed_steps, failed_steps, state_json, created_at, updated_at)
                VALUES ($id, $sid, $ws, $name, $st, $tot, $comp, $fail, $json, $created, $updated)
                ON CONFLICT(id) DO UPDATE SET
                    status = excluded.status,
                    completed_steps = excluded.completed_steps,
                    failed_steps = excluded.failed_steps,
                    state_json = excluded.state_json,
                    updated_at = excluded.updated_at;
                """;
            cmd.Parameters.AddWithValue("$id", wf.Id);
            cmd.Parameters.AddWithValue("$sid", wf.SessionId);
            cmd.Parameters.AddWithValue("$ws", wf.Workspace);
            cmd.Parameters.AddWithValue("$name", wf.Name);
            cmd.Parameters.AddWithValue("$st", wf.Status);
            cmd.Parameters.AddWithValue("$tot", wf.TotalSteps);
            cmd.Parameters.AddWithValue("$comp", wf.CompletedSteps);
            cmd.Parameters.AddWithValue("$fail", wf.FailedSteps);
            cmd.Parameters.AddWithValue("$json", safeJson);
            cmd.Parameters.AddWithValue("$created", wf.CreatedAtUtc.ToString("O"));
            cmd.Parameters.AddWithValue("$updated", wf.UpdatedAtUtc.ToString("O"));
            cmd.ExecuteNonQuery();
        }
    }

    public WorkflowRecord? GetWorkflow(string workflowId)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, session_id, workspace, name, status, total_steps, completed_steps, failed_steps, state_json, created_at, updated_at
            FROM workflow_runs WHERE id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", workflowId);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new WorkflowRecord(
            r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4),
            r.GetInt32(5), r.GetInt32(6), r.GetInt32(7), r.GetString(8),
            DateTimeOffset.Parse(r.GetString(9)), DateTimeOffset.Parse(r.GetString(10)));
    }

    public void RegisterLog(string execId, string sessionId, string filePath, string category, int totalLines, long totalBytes, bool truncated)
    {
        lock (_writeLock)
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT OR REPLACE INTO log_index(exec_id, session_id, file_path, command_category, total_lines, total_bytes, truncated, created_at)
                VALUES ($id, $sid, $fp, $cat, $lines, $bytes, $trunc, $now);
                """;
            cmd.Parameters.AddWithValue("$id", execId);
            cmd.Parameters.AddWithValue("$sid", sessionId);
            cmd.Parameters.AddWithValue("$fp", filePath);
            cmd.Parameters.AddWithValue("$cat", category);
            cmd.Parameters.AddWithValue("$lines", totalLines);
            cmd.Parameters.AddWithValue("$bytes", totalBytes);
            cmd.Parameters.AddWithValue("$trunc", truncated ? 1 : 0);
            cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            cmd.ExecuteNonQuery();
        }
    }

    public (string SessionId, string FilePath, int TotalLines, long TotalBytes, bool Truncated, string Category)? GetLogMeta(string execId)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT session_id, file_path, total_lines, total_bytes, truncated, command_category FROM log_index WHERE exec_id = $id;";
        cmd.Parameters.AddWithValue("$id", execId);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return (r.GetString(0), r.GetString(1), r.GetInt32(2), r.GetInt64(3), r.GetInt32(4) == 1, r.GetString(5));
    }

    public void PruneOldSessions()
    {
        lock (_writeLock)
        {
            using var conn = OpenConnection();
            PruneOldSessionsLocked(conn);
        }
    }

    private void PruneOldSessionsLocked(SqliteConnection conn)
    {
        var stale = new List<string>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT s.id FROM sessions s WHERE s.id <> $active
                AND NOT EXISTS (
                    SELECT 1 FROM edit_journal e
                    WHERE e.session_id = s.id AND e.reverted = 0
                )
                AND NOT EXISTS (
                    SELECT 1 FROM jobs j
                    WHERE j.session_id = s.id AND j.status IN ('queued','running')
                )
                AND NOT EXISTS (
                    SELECT 1 FROM workflow_runs w
                    WHERE w.session_id = s.id AND w.status = 'running'
                )
                AND NOT EXISTS (
                    SELECT 1 FROM operation_ledger o
                    WHERE o.session_id = s.id AND o.status = 'running'
                )
                ORDER BY s.updated_at DESC LIMIT -1 OFFSET $max;
                """;
            cmd.Parameters.AddWithValue("$active", _activeSessionId);
            cmd.Parameters.AddWithValue("$max", _retention.MaxSessionCount);
            using var r = cmd.ExecuteReader();
            while (r.Read()) stale.Add(r.GetString(0));
        }

        foreach (var sid in stale)
        {
            using var del = conn.CreateCommand();
            del.CommandText = """
                DELETE FROM job_events WHERE job_id IN (SELECT id FROM jobs WHERE session_id = $sid);
                DELETE FROM jobs WHERE session_id = $sid;
                DELETE FROM tool_calls WHERE session_id = $sid;
                DELETE FROM edit_reversal_links WHERE
                    original_edit_id IN (SELECT edit_id FROM edit_journal WHERE session_id = $sid)
                    OR inverse_edit_id IN (SELECT edit_id FROM edit_journal WHERE session_id = $sid);
                DELETE FROM edit_journal WHERE session_id = $sid;
                DELETE FROM git_checkpoints WHERE session_id = $sid;
                DELETE FROM session_change_summaries WHERE session_id = $sid;
                DELETE FROM operation_ledger WHERE session_id = $sid;
                DELETE FROM workflow_runs WHERE session_id = $sid;
                DELETE FROM log_index WHERE session_id = $sid;
                DELETE FROM sessions WHERE id = $sid;
                """;
            del.Parameters.AddWithValue("$sid", sid);
            del.ExecuteNonQuery();

            var dir = Path.Combine(_paths.SessionsDir, PortablePaths.SanitizeId(sid));
            if (Directory.Exists(dir))
            {
                try { Directory.Delete(dir, recursive: true); } catch { }
            }
        }
    }

    public void Dispose()
    {
        lock (_writeLock)
        {
            if (_disposed) return;
            try
            {
                using var conn = OpenConnection();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    UPDATE sessions SET status = 'closed', updated_at = $now WHERE status = 'active';
                    PRAGMA wal_checkpoint(TRUNCATE);
                    """;
                cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
                cmd.ExecuteNonQuery();
            }
            catch { }
            _disposed = true;
        }
    }
}
