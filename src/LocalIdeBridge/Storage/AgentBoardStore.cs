using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using LocalIdeBridge.Core;
using Microsoft.Data.Sqlite;

namespace LocalIdeBridge.Storage;

public sealed record BoardRoom(string Id, string Name, string Workspace, DateTimeOffset CreatedAt);
public sealed record BoardAgent(string Id, string RoomId, string Name, string Role, long Generation, DateTimeOffset LastSeenAt);
public sealed record BoardEvent(long Sequence, string RoomId, string Author, string? Target, string Kind, string Body, DateTimeOffset CreatedAt);
public sealed record BoardTask(string Id, string RoomId, string Title, string Details, string Status,
    string? OwnerId, long Revision, long OwnerGeneration, DateTimeOffset? LeaseUntil,
    string? WorkArea, string? Evidence, DateTimeOffset UpdatedAt);
public sealed record BoardPin(string Id, string RoomId, string Key, string Title, string Body, long Revision, DateTimeOffset UpdatedAt);

/// <summary>
/// Durable, paged coordination for independent AI clients. The board is not an inference
/// process: messages are queued until an active agent reads and acknowledges them.
/// </summary>
public sealed class AgentBoardStore
{
    private readonly string _connectionString;
    private readonly CredentialStore _secrets;
    private readonly object _writes = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _signals = new();

    private static TaskCompletionSource<bool> NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void Signal(string roomId)
    {
        _signals.AddOrUpdate(roomId, _ => NewSignal(), (_, previous) =>
        {
            previous.TrySetResult(true);
            return NewSignal();
        });
    }

    public AgentBoardStore(PortablePaths paths, CredentialStore secrets)
    {
        _secrets = secrets;
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = paths.StateDbFile,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        };
        _connectionString = builder.ToString();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var setup = connection.CreateCommand();
        setup.CommandText = "PRAGMA foreign_keys=ON; PRAGMA synchronous=FULL; PRAGMA busy_timeout=5000;";
        setup.ExecuteNonQuery();
        return connection;
    }

    private static SqliteCommand Cmd(SqliteConnection connection, SqliteTransaction? transaction,
        string sql, params (string Key, object? Value)[] values)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (key, value) in values)
            command.Parameters.AddWithValue(key, value ?? DBNull.Value);
        return command;
    }

    private static string Utc() => DateTimeOffset.UtcNow.ToString("O");
    private static DateTimeOffset Time(string value) => DateTimeOffset.Parse(value);
    private static string NewId(string prefix) => prefix + "_" + Guid.NewGuid().ToString("N");
    private static string Required(string value, string label, int max)
    {
        value = value.Trim();
        if (value.Length == 0 || value.Length > max || value.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t')))
            throw new ArgumentException($"{label} must contain 1 to {max} characters without control characters.");
        return value;
    }

    public void Initialize()
    {
        lock (_writes)
        {
            using var conn = Open();
            using var command = conn.CreateCommand();
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS board_rooms (
                    id TEXT PRIMARY KEY, name TEXT NOT NULL, workspace TEXT NOT NULL,
                    created_at TEXT NOT NULL, UNIQUE(workspace, name)
                );
                CREATE TABLE IF NOT EXISTS board_agents (
                    id TEXT PRIMARY KEY, room_id TEXT NOT NULL REFERENCES board_rooms(id),
                    name TEXT NOT NULL, role TEXT NOT NULL, resume_hash BLOB NOT NULL,
                    generation INTEGER NOT NULL, acknowledged_sequence INTEGER NOT NULL DEFAULT 0,
                    last_seen_at TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_board_agents_room ON board_agents(room_id);
                CREATE TABLE IF NOT EXISTS board_events (
                    sequence INTEGER PRIMARY KEY AUTOINCREMENT,
                    room_id TEXT NOT NULL REFERENCES board_rooms(id),
                    author_id TEXT NOT NULL, target_id TEXT,
                    kind TEXT NOT NULL, body TEXT NOT NULL,
                    request_id TEXT, created_at TEXT NOT NULL,
                    UNIQUE(room_id, author_id, request_id)
                );
                CREATE INDEX IF NOT EXISTS idx_board_events_room ON board_events(room_id, sequence);
                CREATE INDEX IF NOT EXISTS idx_board_events_target ON board_events(room_id, target_id, sequence);
                CREATE TABLE IF NOT EXISTS board_tasks (
                    id TEXT PRIMARY KEY, room_id TEXT NOT NULL REFERENCES board_rooms(id),
                    title TEXT NOT NULL, details TEXT NOT NULL, status TEXT NOT NULL,
                    owner_id TEXT, owner_generation INTEGER NOT NULL DEFAULT 0,
                    revision INTEGER NOT NULL DEFAULT 1, lease_until TEXT,
                    work_area TEXT, evidence TEXT, updated_at TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_board_tasks_room ON board_tasks(room_id, status, updated_at DESC);
                CREATE TABLE IF NOT EXISTS board_task_requests (
                    room_id TEXT NOT NULL, author_id TEXT NOT NULL, request_id TEXT NOT NULL,
                    task_id TEXT NOT NULL REFERENCES board_tasks(id), content_hash TEXT NOT NULL,
                    PRIMARY KEY(room_id,author_id,request_id)
                );
                CREATE TABLE IF NOT EXISTS board_pins (
                    id TEXT PRIMARY KEY, room_id TEXT NOT NULL REFERENCES board_rooms(id),
                    pin_key TEXT NOT NULL, title TEXT NOT NULL, body TEXT NOT NULL,
                    revision INTEGER NOT NULL DEFAULT 1, updated_at TEXT NOT NULL,
                    UNIQUE(room_id, pin_key)
                );
                """;
            command.ExecuteNonQuery();
        }
    }

    private static BoardRoom ReadRoom(SqliteDataReader r) =>
        new(r.GetString(0), r.GetString(1), r.GetString(2), Time(r.GetString(3)));

    private static BoardAgent ReadAgent(SqliteDataReader r) =>
        new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt64(4), Time(r.GetString(5)));

    private static BoardTask ReadTask(SqliteDataReader r) =>
        new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4),
            r.IsDBNull(5) ? null : r.GetString(5), r.GetInt64(6), r.GetInt64(7),
            r.IsDBNull(8) ? null : Time(r.GetString(8)),
            r.IsDBNull(9) ? null : r.GetString(9),
            r.IsDBNull(10) ? null : r.GetString(10), Time(r.GetString(11)));

    public IReadOnlyList<BoardRoom> ListRooms(string workspace, int limit = 40)
    {
        using var conn = Open();
        using var cmd = Cmd(conn, null, """
            SELECT id,name,workspace,created_at FROM board_rooms WHERE workspace=$ws
            ORDER BY created_at ASC LIMIT $limit;
            """, ("$ws", WorkspaceGuard.NormalizeRoot(workspace)),
            ("$limit", Math.Clamp(limit, 1, 100)));
        using var r = cmd.ExecuteReader();
        var result = new List<BoardRoom>();
        while (r.Read()) result.Add(ReadRoom(r));
        return result;
    }

    public BoardRoom GetOrCreateRoom(string name, string workspace)
    {
        name = Required(name, "Room name", 64);
        workspace = WorkspaceGuard.NormalizeRoot(workspace);
        lock (_writes)
        {
            using var conn = Open();
            using (var insert = Cmd(conn, null, """
                INSERT INTO board_rooms(id,name,workspace,created_at) VALUES($id,$name,$ws,$now)
                ON CONFLICT(workspace,name) DO NOTHING;
                """, ("$id", NewId("room")), ("$name", name), ("$ws", workspace), ("$now", Utc())))
                insert.ExecuteNonQuery();
            using var query = Cmd(conn, null,
                "SELECT id,name,workspace,created_at FROM board_rooms WHERE workspace=$ws AND name=$name;",
                ("$ws", workspace), ("$name", name));
            using var r = query.ExecuteReader();
            if (!r.Read()) throw new InvalidOperationException("Could not find newly created board room.");
            return ReadRoom(r);
        }
    }

    public BoardRoom RequireRoom(string roomId, string workspace)
    {
        using var conn = Open();
        using var command = Cmd(conn, null,
            "SELECT id,name,workspace,created_at FROM board_rooms WHERE id=$id;", ("$id", roomId));
        using var r = command.ExecuteReader();
        if (!r.Read()) throw new KeyNotFoundException("Board room not found.");
        var room = ReadRoom(r);
        if (!string.Equals(room.Workspace, WorkspaceGuard.NormalizeRoot(workspace),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidOperationException("Board room belongs to another workspace.");
        return room;
    }

    private static byte[] Hash(string secret) => SHA256.HashData(Encoding.UTF8.GetBytes(secret));
    private static string NewSecret() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

    /// <summary>Joining an existing identity rotates its credential and invalidates earlier attachments.</summary>
    public (BoardAgent Agent, string Credential) Join(BoardRoom room, string name, string role,
        string? resumeId = null, string? credential = null)
    {
        name = Required(name, "Agent name", 64);
        role = Required(role, "Agent role", 32);
        lock (_writes)
        {
            using var conn = Open();
            using var tx = conn.BeginTransaction();
            var nextSecret = NewSecret();
            var agentId = resumeId is null ? NewId("agent") : Required(resumeId, "Agent ID", 96);
            if (resumeId is not null)
            {
                if (string.IsNullOrEmpty(credential)) throw new UnauthorizedAccessException("Resume credential is required.");
                using var existing = Cmd(conn, tx,
                    "SELECT resume_hash,role FROM board_agents WHERE id=$id AND room_id=$room;",
                    ("$id", agentId), ("$room", room.Id));
                byte[] original;
                string previousRole;
                using (var reader = existing.ExecuteReader())
                {
                    if (!reader.Read()) throw new UnauthorizedAccessException("Unknown agent identity.");
                    original = (byte[])reader.GetValue(0);
                    previousRole = reader.GetString(1);
                }
                if (!CryptographicOperations.FixedTimeEquals(original, Hash(credential)))
                    throw new UnauthorizedAccessException("Invalid agent identity or resume credential.");
                if (!string.Equals(role, previousRole, StringComparison.OrdinalIgnoreCase))
                    throw new UnauthorizedAccessException("Rejoining cannot change an agent's role.");
                using var update = Cmd(conn, tx, """
                    UPDATE board_agents SET name=$name,role=$role,resume_hash=$hash,
                    generation=generation+1,last_seen_at=$now WHERE id=$id AND room_id=$room;
                    """, ("$name", name), ("$role", role), ("$hash", Hash(nextSecret)),
                    ("$now", Utc()), ("$id", agentId), ("$room", room.Id));
                update.ExecuteNonQuery();
            }
            else
            {
                if (role.Equals("coordinator", StringComparison.OrdinalIgnoreCase))
                {
                    using var occupied = Cmd(conn, tx,
                        "SELECT COUNT(1) FROM board_agents WHERE room_id=$room AND lower(role)='coordinator';",
                        ("$room", room.Id));
                    if (Convert.ToInt64(occupied.ExecuteScalar()) > 0)
                        throw new UnauthorizedAccessException("This room already has a coordinator. Resume that identity instead.");
                }
                using var create = Cmd(conn, tx, """
                    INSERT INTO board_agents(id,room_id,name,role,resume_hash,generation,last_seen_at)
                    VALUES($id,$room,$name,$role,$hash,1,$now);
                    """, ("$id", agentId), ("$room", room.Id), ("$name", name),
                    ("$role", role), ("$hash", Hash(nextSecret)), ("$now", Utc()));
                create.ExecuteNonQuery();
            }
            using var query = Cmd(conn, tx,
                "SELECT id,room_id,name,role,generation,last_seen_at FROM board_agents WHERE id=$id;",
                ("$id", agentId));
            BoardAgent agent;
            using (var r = query.ExecuteReader())
            {
                if (!r.Read()) throw new InvalidOperationException("Joined agent could not be retrieved.");
                agent = ReadAgent(r);
            }
            tx.Commit();
            Signal(room.Id);
            return (agent, nextSecret);
        }
    }

    private static BoardAgent ValidateAgent(SqliteConnection conn, SqliteTransaction? tx,
        string roomId, string agentId, string credential)
    {
        using var cmd = Cmd(conn, tx, """
            SELECT id,room_id,name,role,generation,last_seen_at,resume_hash
            FROM board_agents WHERE id=$id AND room_id=$room;
            """, ("$id", agentId), ("$room", roomId));
        using var r = cmd.ExecuteReader();
        if (!r.Read()) throw new UnauthorizedAccessException("Unknown board agent.");
        var agent = ReadAgent(r);
        if (credential.Length is < 1 or > 256 ||
            !CryptographicOperations.FixedTimeEquals((byte[])r.GetValue(6), Hash(credential)))
            throw new UnauthorizedAccessException("Board agent credential expired or invalid; rejoin to continue.");
        return agent;
    }

    public BoardAgent Authenticate(BoardRoom room, string agentId, string credential)
    {
        using var conn = Open();
        return ValidateAgent(conn, null, room.Id, agentId, credential);
    }

    public IReadOnlyList<BoardAgent> ListAgents(BoardRoom room, int limit = 40)
    {
        using var conn = Open();
        using var cmd = Cmd(conn, null, """
            SELECT id,room_id,name,role,generation,last_seen_at
            FROM board_agents WHERE room_id=$room ORDER BY last_seen_at DESC LIMIT $limit;
            """, ("$room", room.Id), ("$limit", Math.Clamp(limit, 1, 100)));
        using var r = cmd.ExecuteReader();
        var result = new List<BoardAgent>();
        while (r.Read()) result.Add(ReadAgent(r));
        return result;
    }

    public BoardEvent Post(BoardRoom room, string agentId, string credential,
        string kind, string body, string requestId, string? targetId = null)
    {
        kind = Required(kind, "Message kind", 32);
        if (kind is not ("message" or "steering" or "blocker" or "decision" or "review_request" or "status"))
            throw new ArgumentException("Unsupported board message kind.");
        body = Required(body, "Message body", 4000);
        requestId = Required(requestId, "Request ID", 128);
        lock (_writes)
        {
            using var conn = Open();
            using var tx = conn.BeginTransaction();
            ValidateAgent(conn, tx, room.Id, agentId, credential);
            if (!string.IsNullOrEmpty(targetId))
            {
                using var target = Cmd(conn, tx, "SELECT COUNT(1) FROM board_agents WHERE room_id=$room AND id=$target;",
                    ("$room", room.Id), ("$target", targetId));
                if (Convert.ToInt64(target.ExecuteScalar()) == 0)
                    throw new ArgumentException("Target agent is not part of this room.");
            }
            var safe = _secrets.Redact(body);
            using (var insert = Cmd(conn, tx, """
                INSERT INTO board_events(room_id,author_id,target_id,kind,body,request_id,created_at)
                VALUES($room,$author,$target,$kind,$body,$key,$now)
                ON CONFLICT(room_id,author_id,request_id) DO NOTHING;
                """, ("$room", room.Id), ("$author", agentId), ("$target", targetId),
                ("$kind", kind), ("$body", safe), ("$key", requestId), ("$now", Utc())))
                insert.ExecuteNonQuery();
            using var query = Cmd(conn, tx, """
                SELECT sequence,room_id,author_id,target_id,kind,body,created_at
                FROM board_events WHERE room_id=$room AND author_id=$author AND request_id=$key;
                """, ("$room", room.Id), ("$author", agentId), ("$key", requestId));
            BoardEvent result;
            using (var r = query.ExecuteReader())
            {
                if (!r.Read()) throw new InvalidOperationException("Message was not recorded.");
                result = ReadEvent(r);
            }
            if (result.Kind != kind || result.Body != safe || result.Target != targetId)
                throw new InvalidOperationException("requestId was reused with different message content.");
            tx.Commit();
            Signal(room.Id);
            return result;
        }
    }

    private static BoardEvent ReadEvent(SqliteDataReader r) =>
        new(r.GetInt64(0), r.GetString(1), r.GetString(2),
            r.IsDBNull(3) ? null : r.GetString(3), r.GetString(4), r.GetString(5), Time(r.GetString(6)));

    public (IReadOnlyList<BoardEvent> Events, bool HasMore) ReadEvents(
        BoardRoom room, long after, int limit, string? target = null)
    {
        limit = Math.Clamp(limit, 1, 100);
        using var conn = Open();
        using var query = Cmd(conn, null, """
            SELECT sequence,room_id,author_id,target_id,kind,body,created_at
            FROM board_events
            WHERE room_id=$room AND sequence>$after
              AND ($target IS NULL OR target_id IS NULL OR target_id=$target OR author_id=$target)
            ORDER BY sequence ASC LIMIT $limit;
            """, ("$room", room.Id), ("$after", Math.Max(0, after)),
            ("$target", target), ("$limit", limit + 1));
        using var r = query.ExecuteReader();
        var result = new List<BoardEvent>();
        while (r.Read()) result.Add(ReadEvent(r));
        var hasMore = result.Count > limit;
        if (hasMore) result.RemoveAt(result.Count - 1);
        return (result, hasMore);
    }

    public long LatestSequence(BoardRoom room, string? agentId = null)
    {
        using var conn = Open();
        using var cmd = Cmd(conn, null, """
            SELECT COALESCE(MAX(sequence),0) FROM board_events
            WHERE room_id=$room AND ($agent IS NULL OR target_id IS NULL OR
                target_id=$agent OR author_id=$agent);
            """, ("$room", room.Id), ("$agent", agentId));
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    /// <summary>No polling loop; each waiter either sees a new event or times out.</summary>
    public async Task<bool> WaitForEventsAsync(BoardRoom room, long after, int waitMs,
        string? agentId, CancellationToken ct)
    {
        var signal = _signals.GetOrAdd(room.Id, _ => NewSignal());
        if (LatestSequence(room, agentId) > after) return true;
        if (waitMs < 1) return false;
        try
        {
            await signal.Task.WaitAsync(TimeSpan.FromMilliseconds(Math.Clamp(waitMs, 1, 20_000)), ct)
                .ConfigureAwait(false);
        }
        catch (TimeoutException) { }
        return LatestSequence(room, agentId) > after;
    }

    public IReadOnlyList<BoardEvent> ReadLatestEvents(BoardRoom room, int limit = 25)
    {
        using var conn = Open();
        using var query = Cmd(conn, null, """
            SELECT sequence,room_id,author_id,target_id,kind,body,created_at
            FROM board_events WHERE room_id=$room ORDER BY sequence DESC LIMIT $limit;
            """, ("$room", room.Id), ("$limit", Math.Clamp(limit, 1, 100)));
        using var r = query.ExecuteReader();
        var result = new List<BoardEvent>();
        while (r.Read()) result.Add(ReadEvent(r));
        result.Reverse();
        return result;
    }

    /// <summary>Local supervisor-only steering; no remote caller may impersonate this author.</summary>
    public BoardEvent PostSupervisor(BoardRoom room, string body, string? targetAgentId = null)
    {
        body = _secrets.Redact(Required(body, "Supervisor message", 4000));
        lock (_writes)
        {
            using var conn = Open();
            using var tx = conn.BeginTransaction();
            if (targetAgentId is not null)
            {
                using var target = Cmd(conn, tx, "SELECT COUNT(1) FROM board_agents WHERE room_id=$room AND id=$id;",
                    ("$room", room.Id), ("$id", targetAgentId));
                if (Convert.ToInt64(target.ExecuteScalar()) != 1)
                    throw new ArgumentException("Target agent not found in this room.");
            }
            using (var command = Cmd(conn, tx, """
                INSERT INTO board_events(room_id,author_id,target_id,kind,body,created_at)
                VALUES($room,'owner',$target,'steering',$body,$now);
                """, ("$room", room.Id), ("$target", targetAgentId), ("$body", body), ("$now", Utc())))
                command.ExecuteNonQuery();
            using var id = Cmd(conn, tx, "SELECT last_insert_rowid();");
            var sequence = Convert.ToInt64(id.ExecuteScalar());
            using var query = Cmd(conn, tx, """
                SELECT sequence,room_id,author_id,target_id,kind,body,created_at
                FROM board_events WHERE sequence=$sequence;
                """, ("$sequence", sequence));
            BoardEvent result;
            using (var r = query.ExecuteReader())
            {
                if (!r.Read()) throw new InvalidOperationException("Supervisor steering event was not saved.");
                result = ReadEvent(r);
            }
            tx.Commit();
            Signal(room.Id);
            return result;
        }
    }

    public void Acknowledge(BoardRoom room, string agentId, string credential, long sequence)
    {
        if (sequence < 0) throw new ArgumentException("Acknowledgment cursor must be nonnegative.");
        lock (_writes)
        {
            using var conn = Open();
            using var tx = conn.BeginTransaction();
            var agent = ValidateAgent(conn, tx, room.Id, agentId, credential);
            if (sequence > 0)
            {
                var visibleTo = agent.Role.Equals("coordinator", StringComparison.OrdinalIgnoreCase) ? null : agentId;
                using var check = Cmd(conn, tx, """
                    SELECT COUNT(1) FROM board_events WHERE room_id=$room AND sequence=$seq
                    AND ($agent IS NULL OR target_id IS NULL OR target_id=$agent OR author_id=$agent);
                    """, ("$room", room.Id), ("$seq", sequence), ("$agent", visibleTo));
                if (Convert.ToInt64(check.ExecuteScalar()) != 1)
                    throw new ArgumentException("This event was not visible to this agent; it cannot be acknowledged.");
            }
            using var update = Cmd(conn, tx, """
                UPDATE board_agents SET acknowledged_sequence=MAX(acknowledged_sequence,$seq),last_seen_at=$now
                WHERE id=$id AND room_id=$room;
                """, ("$seq", sequence), ("$now", Utc()), ("$id", agentId), ("$room", room.Id));
            update.ExecuteNonQuery();
            tx.Commit();
            Signal(room.Id);
        }
    }

    public long GetAcknowledged(BoardRoom room, string agentId)
    {
        using var conn = Open();
        using var query = Cmd(conn, null,
            "SELECT acknowledged_sequence FROM board_agents WHERE room_id=$room AND id=$id;",
            ("$room", room.Id), ("$id", agentId));
        var result = query.ExecuteScalar();
        return result is null ? 0 : Convert.ToInt64(result);
    }

    public BoardPin SavePin(BoardRoom room, string agentId, string credential, string key,
        string title, string body, long? expectedRevision)
    {
        key = Required(key, "Pin key", 64);
        title = Required(title, "Pin title", 120);
        body = _secrets.Redact(Required(body, "Pin body", 4000));
        lock (_writes)
        {
            using var conn = Open();
            using var tx = conn.BeginTransaction();
            ValidateAgent(conn, tx, room.Id, agentId, credential);
            using var currentPin = Cmd(conn, tx,
                "SELECT revision FROM board_pins WHERE room_id=$room AND pin_key=$key;",
                ("$room", room.Id), ("$key", key));
            var oldRevision = currentPin.ExecuteScalar();
            if ((oldRevision is null) != (expectedRevision is null) ||
                (oldRevision is not null && Convert.ToInt64(oldRevision) != expectedRevision))
                throw new InvalidOperationException("Pin revision conflict; sync before updating.");
            using var command = Cmd(conn, tx, """
                INSERT INTO board_pins(id,room_id,pin_key,title,body,revision,updated_at)
                VALUES($id,$room,$key,$title,$body,1,$now)
                ON CONFLICT(room_id,pin_key) DO UPDATE SET
                title=excluded.title,body=excluded.body,revision=revision+1,updated_at=excluded.updated_at
                WHERE revision=$expected;
                """, ("$id", NewId("pin")), ("$room", room.Id), ("$key", key),
                ("$title", title), ("$body", body), ("$now", Utc()),
                ("$expected", expectedRevision));
            if (command.ExecuteNonQuery() != 1)
                throw new InvalidOperationException("Pin revision conflict; sync before updating.");
            using var find = Cmd(conn, tx, """
                SELECT id,room_id,pin_key,title,body,revision,updated_at
                FROM board_pins WHERE room_id=$room AND pin_key=$key;
                """, ("$room", room.Id), ("$key", key));
            BoardPin result;
            using (var r = find.ExecuteReader())
            {
                if (!r.Read()) throw new InvalidOperationException("Pin not found after update.");
                result = ReadPin(r);
            }
            using (var eventCommand = Cmd(conn, tx, """
                INSERT INTO board_events(room_id,author_id,kind,body,created_at)
                VALUES($room,$author,'decision',$body,$now);
                """, ("$room", room.Id), ("$author", agentId),
                ("$body", $"Updated pin {key}: {title}"), ("$now", Utc())))
                eventCommand.ExecuteNonQuery();
            tx.Commit();
            Signal(room.Id);
            return result;
        }
    }

    private static BoardPin ReadPin(SqliteDataReader r) =>
        new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3),
            r.GetString(4), r.GetInt64(5), Time(r.GetString(6)));

    public void DeletePin(BoardRoom room, string agentId, string credential,
        string key, long expectedRevision)
    {
        key = Required(key, "Pin key", 64);
        lock (_writes)
        {
            using var conn = Open();
            using var tx = conn.BeginTransaction();
            ValidateAgent(conn, tx, room.Id, agentId, credential);
            using (var command = Cmd(conn, tx, """
                DELETE FROM board_pins WHERE room_id=$room AND pin_key=$key AND revision=$revision;
                """, ("$room", room.Id), ("$key", key), ("$revision", expectedRevision)))
                if (command.ExecuteNonQuery() != 1)
                    throw new InvalidOperationException("Pin revision changed or pin not found; sync first.");
            using (var eventCommand = Cmd(conn, tx, """
                INSERT INTO board_events(room_id,author_id,kind,body,created_at)
                VALUES($room,$author,'decision',$body,$now);
                """, ("$room", room.Id), ("$author", agentId),
                ("$body", $"Removed pin {key}"), ("$now", Utc())))
                eventCommand.ExecuteNonQuery();
            tx.Commit();
            Signal(room.Id);
        }
    }

    public IReadOnlyList<BoardPin> ListPins(BoardRoom room, int limit = 50)
    {
        using var conn = Open();
        using var query = Cmd(conn, null, """
            SELECT id,room_id,pin_key,title,body,revision,updated_at
            FROM board_pins WHERE room_id=$room ORDER BY updated_at DESC LIMIT $limit;
            """, ("$room", room.Id), ("$limit", Math.Clamp(limit, 1, 100)));
        using var r = query.ExecuteReader();
        var list = new List<BoardPin>();
        while (r.Read()) list.Add(ReadPin(r));
        return list;
    }

    private static string WorkAreaPath(BoardRoom room, string? workArea)
    {
        if (string.IsNullOrWhiteSpace(workArea))
            return room.Workspace;
        if (Path.IsPathRooted(workArea))
            throw new ArgumentException("Task work area must be relative to the workspace.");

        var full = Path.GetFullPath(Path.Combine(room.Workspace, workArea));
        if (!WorkspaceGuard.IsEqualOrInside(full, room.Workspace))
            throw new ArgumentException("Task work area cannot escape the trusted workspace.");
        return full;
    }

    private static void EnsureClaimDoesNotOverlap(SqliteConnection conn, SqliteTransaction tx,
        BoardRoom room, BoardTask task)
    {
        var requested = WorkAreaPath(room, task.WorkArea);
        using var query = Cmd(conn, tx, """
            SELECT t.id,t.work_area
            FROM board_tasks t JOIN board_rooms r ON r.id=t.room_id
            WHERE r.workspace=$workspace AND t.status='in_progress'
                AND t.id<>$task;
            """, ("$workspace", room.Workspace), ("$task", task.Id));
        using var reader = query.ExecuteReader();
        while (reader.Read())
        {
            var other = reader.GetString(0);
            var area = reader.IsDBNull(1) ? null : reader.GetString(1);
            var existing = WorkAreaPath(room, area);
            if (WorkspaceGuard.IsEqualOrInside(requested, existing) ||
                WorkspaceGuard.IsEqualOrInside(existing, requested))
                throw new InvalidOperationException(
                    $"Task work area conflicts with in-progress task {other}. Review and release that task before taking ownership.");
        }
    }

    public BoardTask CreateTask(BoardRoom room, string agentId, string credential,
        string title, string details, string? workArea, string requestId)
    {
        title = Required(title, "Task title", 180);
        details = _secrets.Redact(Required(details, "Task details", 4000));
        workArea = string.IsNullOrWhiteSpace(workArea) ? null : Required(workArea, "Work area", 240);
        _ = WorkAreaPath(room, workArea);
        requestId = Required(requestId, "Task request ID", 128);
        var inputHash = Convert.ToHexStringLower(Hash(title + "\0" + details + "\0" + workArea));
        lock (_writes)
        {
            using var conn = Open();
            using var tx = conn.BeginTransaction();
            ValidateAgent(conn, tx, room.Id, agentId, credential);
            using (var prior = Cmd(conn, tx, """
                SELECT task_id,content_hash FROM board_task_requests
                WHERE room_id=$room AND author_id=$author AND request_id=$key;
                """, ("$room", room.Id), ("$author", agentId), ("$key", requestId)))
            using (var existing = prior.ExecuteReader())
            {
                if (existing.Read())
                {
                    var oldTaskId = existing.GetString(0);
                    if (existing.GetString(1) != inputHash)
                        throw new InvalidOperationException("Task requestId already used with different content.");
                    existing.Close();
                    var oldTask = ReadTaskById(conn, tx, room.Id, oldTaskId);
                    tx.Commit();
                    return oldTask;
                }
            }
            var id = NewId("task");
            using (var insert = Cmd(conn, tx, """
                INSERT INTO board_tasks(id,room_id,title,details,status,revision,work_area,updated_at)
                VALUES($id,$room,$title,$details,'open',1,$area,$now);
                """, ("$id", id), ("$room", room.Id), ("$title", title),
                ("$details", details), ("$area", workArea), ("$now", Utc())))
                insert.ExecuteNonQuery();
            using (var receipt = Cmd(conn, tx, """
                INSERT INTO board_task_requests(room_id,author_id,request_id,task_id,content_hash)
                VALUES($room,$author,$key,$task,$hash);
                """, ("$room", room.Id), ("$author", agentId), ("$key", requestId),
                ("$task", id), ("$hash", inputHash)))
                receipt.ExecuteNonQuery();
            using (var eventCommand = Cmd(conn, tx, """
                INSERT INTO board_events(room_id,author_id,kind,body,created_at)
                VALUES($room,$author,'status',$body,$now);
                """, ("$room", room.Id), ("$author", agentId),
                ("$body", $"Created task {id}: {title}"), ("$now", Utc())))
                eventCommand.ExecuteNonQuery();
            var result = ReadTaskById(conn, tx, room.Id, id);
            tx.Commit();
            Signal(room.Id);
            return result;
        }
    }

    private static BoardTask ReadTaskById(SqliteConnection conn, SqliteTransaction? tx, string roomId, string id)
    {
        using var cmd = Cmd(conn, tx, """
            SELECT id,room_id,title,details,status,owner_id,revision,owner_generation,
                   lease_until,work_area,evidence,updated_at
            FROM board_tasks WHERE room_id=$room AND id=$id;
            """, ("$room", roomId), ("$id", id));
        using var r = cmd.ExecuteReader();
        if (!r.Read()) throw new KeyNotFoundException("Board task not found.");
        return ReadTask(r);
    }

    public IReadOnlyList<BoardTask> ListTasks(BoardRoom room, int limit = 40, int offset = 0)
    {
        using var conn = Open();
        using var cmd = Cmd(conn, null, """
            SELECT id,room_id,title,details,status,owner_id,revision,owner_generation,
                   lease_until,work_area,evidence,updated_at
            FROM board_tasks WHERE room_id=$room
            ORDER BY CASE status WHEN 'blocked' THEN 0 WHEN 'in_progress' THEN 1 ELSE 2 END, updated_at DESC
            LIMIT $limit OFFSET $offset;
            """, ("$room", room.Id), ("$limit", Math.Clamp(limit, 1, 100)),
            ("$offset", Math.Clamp(offset, 0, 1_000_000)));
        using var r = cmd.ExecuteReader();
        var result = new List<BoardTask>();
        while (r.Read()) result.Add(ReadTask(r));
        return result;
    }

    /// <summary>
    /// Task transitions are compare-and-swap operations. Expired leases are NOT silently
    /// stolen: a coordinator must inspect the workspace and explicitly release/reassign.
    /// </summary>
    public BoardTask RequireActiveClaim(BoardRoom room, string agentId, string credential, string taskId)
    {
        using var conn = Open();
        var agent = ValidateAgent(conn, null, room.Id, agentId, credential);
        var task = ReadTaskById(conn, null, room.Id, taskId);
        if (task.Status != "in_progress" || task.OwnerId != agent.Id ||
            task.OwnerGeneration != agent.Generation || task.LeaseUntil <= DateTimeOffset.UtcNow)
            throw new UnauthorizedAccessException(
                "This task is not claimed by your current attachment with an active lease. Sync and reconcile before editing.");
        return task;
    }

    public BoardTask ChangeTask(BoardRoom room, string agentId, string credential,
        string taskId, string action, long expectedRevision, string? evidence = null,
        string? assigneeId = null, int leaseMinutes = 15,
        string? title = null, string? details = null)
    {
        if (expectedRevision < 1) throw new ArgumentException("expectedRevision must be positive.");
        if (action is not ("claim" or "renew" or "release" or "complete" or "block" or "reopen" or "assign" or "adopt" or "revise"))
            throw new ArgumentException("Unknown task action.");
        if (evidence is not null) evidence = _secrets.Redact(Required(evidence, "Evidence", 2000));
        lock (_writes)
        {
            using var conn = Open();
            using var tx = conn.BeginTransaction();
            var agent = ValidateAgent(conn, tx, room.Id, agentId, credential);
            var current = ReadTaskById(conn, tx, room.Id, taskId);
            if (current.Revision != expectedRevision)
                throw new InvalidOperationException("Task revision changed; sync before retrying.");
            var coordinator = agent.Role.Equals("coordinator", StringComparison.OrdinalIgnoreCase);
            var owned = current.OwnerId == agentId && current.OwnerGeneration == agent.Generation;
            var status = current.Status;
            var nextTitle = current.Title;
            var nextDetails = current.Details;
            var owner = current.OwnerId;
            var generation = current.OwnerGeneration;
            DateTimeOffset? lease = current.LeaseUntil;
            switch (action)
            {
                case "claim":
                    if (status != "open" || owner is not null)
                        throw new InvalidOperationException("Task already claimed or not open; do not steal an expired lease.");
                    EnsureClaimDoesNotOverlap(conn, tx, room, current);
                    status = "in_progress"; owner = agentId; generation = agent.Generation;
                    lease = DateTimeOffset.UtcNow.AddMinutes(Math.Clamp(leaseMinutes, 1, 60));
                    break;
                case "revise":
                    if (!owned && !coordinator)
                        throw new UnauthorizedAccessException("Only the owner or coordinator can revise this task.");
                    if (owned && lease <= DateTimeOffset.UtcNow)
                        throw new InvalidOperationException("Task lease expired; reconcile before revising.");
                    if (title is null && details is null)
                        throw new ArgumentException("Provide a new title or details to revise the task.");
                    if (title is not null) nextTitle = Required(title, "Task title", 180);
                    if (details is not null) nextDetails = _secrets.Redact(Required(details, "Task details", 4000));
                    break;
                case "adopt":
                    if (current.OwnerId != agentId || current.OwnerGeneration >= agent.Generation ||
                        current.Status != "in_progress" || string.IsNullOrWhiteSpace(evidence))
                        throw new InvalidOperationException(
                            "Can only adopt your own stale attachment after inspecting prior work and supplying reconciliation evidence.");
                    generation = agent.Generation;
                    lease = DateTimeOffset.UtcNow.AddMinutes(Math.Clamp(leaseMinutes, 1, 60));
                    break;
                case "renew":
                    if (!owned || status != "in_progress" || lease <= DateTimeOffset.UtcNow)
                        throw new InvalidOperationException("Task lease expired or belongs to another attachment; reconcile first.");
                    lease = DateTimeOffset.UtcNow.AddMinutes(Math.Clamp(leaseMinutes, 1, 60));
                    break;
                case "release":
                case "reopen":
                    if (!owned && !coordinator) throw new UnauthorizedAccessException("Only the owner or coordinator can release a task.");
                    status = "open"; owner = null; generation = 0; lease = null;
                    break;
                case "complete":
                case "block":
                    if (!owned && !coordinator) throw new UnauthorizedAccessException("Only the owner or coordinator can update this task.");
                    if (owned && lease <= DateTimeOffset.UtcNow)
                        throw new InvalidOperationException("Lease expired; reconcile before changing task state.");
                    status = action == "complete" ? "done" : "blocked"; lease = null;
                    break;
                case "assign":
                    if (!coordinator) throw new UnauthorizedAccessException("Only the coordinator can assign tasks.");
                    EnsureClaimDoesNotOverlap(conn, tx, room, current);
                    if (string.IsNullOrWhiteSpace(assigneeId))
                        throw new ArgumentException("assigneeId is required for assign.");
                    using (var target = Cmd(conn, tx,
                        "SELECT generation FROM board_agents WHERE room_id=$room AND id=$id;",
                        ("$room", room.Id), ("$id", assigneeId)))
                    {
                        var value = target.ExecuteScalar();
                        if (value is null) throw new ArgumentException("Target agent is not part of the room.");
                        generation = Convert.ToInt64(value);
                    }
                    if (current.OwnerId is not null && string.IsNullOrWhiteSpace(evidence))
                        throw new InvalidOperationException("Reassignment requires reconciliation evidence when another agent owns this task.");
                    status = "in_progress"; owner = assigneeId;
                    lease = DateTimeOffset.UtcNow.AddMinutes(Math.Clamp(leaseMinutes, 1, 60));
                    break;
            }
            using (var update = Cmd(conn, tx, """
                UPDATE board_tasks SET title=$title,details=$details,status=$status,
                    owner_id=$owner,owner_generation=$generation,
                    lease_until=$lease,evidence=COALESCE($evidence,evidence),revision=revision+1,
                    updated_at=$now
                WHERE id=$id AND room_id=$room AND revision=$revision;
                """, ("$title", nextTitle), ("$details", nextDetails),
                ("$status", status), ("$owner", owner), ("$generation", generation),
                ("$lease", lease?.ToString("O")), ("$evidence", evidence), ("$now", Utc()),
                ("$id", taskId), ("$room", room.Id), ("$revision", expectedRevision)))
            {
                if (update.ExecuteNonQuery() != 1) throw new InvalidOperationException("Concurrent task change; sync first.");
            }
            using (var eventCommand = Cmd(conn, tx, """
                INSERT INTO board_events(room_id,author_id,kind,body,created_at)
                VALUES($room,$author,'status',$body,$now);
                """, ("$room", room.Id), ("$author", agentId),
                ("$body", $"Task {taskId}: {action} -> {status}; {evidence ?? "no evidence supplied"}"),
                ("$now", Utc())))
                eventCommand.ExecuteNonQuery();
            var updated = ReadTaskById(conn, tx, room.Id, taskId);
            tx.Commit();
            Signal(room.Id);
            return updated;
        }
    }
}
