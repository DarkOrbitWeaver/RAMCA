using System.Text;
using System.Text.Json.Nodes;
using LocalIdeBridge.Core;
using LocalIdeBridge.Storage;

namespace LocalIdeBridge.Tools;

/// <summary>
/// Shared coordination tools for multiple independent ChatGPT/MCP conversations.
/// All messages and task claims survive chat replacement and RAMCA restart.
/// </summary>
public static class AgentBoardTools
{
    public static IEnumerable<ToolDefinition> Create(WorkspaceGuard guard, AgentBoardStore board,
        StateDatabase database, ToolRegistry registry)
    {
        BoardRoom Room(JsonObject args) => board.RequireRoom(args.RequireString("roomId"),
            guard.RequireActiveWorkspace());

        BoardAgent Agent(JsonObject args, BoardRoom room) =>
            board.Authenticate(room, args.RequireString("agentId"), args.RequireString("credential"));

        yield return new ToolDefinition(
            Name: "board_join", Category: "board",
            Description: "Join or resume a durable project chatboard. Return a scoped agent identity and credential. New chats can resume an existing agent with agentId and credential.",
            IsReadOnly: false, IsIdempotent: false,
            SideEffects: "Creates or resumes a durable agent; resuming rotates its credential and invalidates the prior attachment.",
            Parameters:
            [
                new("room", "string", "Project room name (or an existing roomId).", Required: true),
                new("name", "string", "Short agent name, such as Engine, UI, Reviewer or Lead.", Required: true),
                new("role", "string", "Agent role for direct joins: worker, reviewer or coordinator. Omit when using inviteCode."),
                new("agentId", "string", "Existing agentId to resume (omit for a new identity)."),
                new("credential", "string", "Existing scoped agent credential required when resuming."),
                new("inviteCode", "string", "Optional one-time coordinator invitation for new or replacement agent.")
            ],
            Handler: (args, _, _) =>
            {
                var workspace = guard.RequireActiveWorkspace();
                var identifier = args.RequireString("room");
                var room = identifier.StartsWith("room_", StringComparison.Ordinal)
                    ? board.RequireRoom(identifier, workspace)
                    : board.GetOrCreateRoom(identifier, workspace);
                var inviteCode = args.OptionalString("inviteCode");
                if (inviteCode is not null && (args.OptionalString("agentId") is not null ||
                    args.OptionalString("credential") is not null))
                    throw new ArgumentException("Use either inviteCode or agentId/credential, not both.");
                var (agent, secret) = inviteCode is null
                    ? board.Join(room, args.RequireString("name"), args.RequireString("role"),
                        args.OptionalString("agentId"), args.OptionalString("credential"))
                    : board.RedeemInvitation(room, args.RequireString("name"), inviteCode);
                return Task.FromResult(ToolResult.Ok($"Joined room '{room.Name}' as {agent.Name}.", new
                {
                    room, agent,
                    credential = secret,
                    warning = "Save this scoped credential privately for board calls and future reattachment. It is not the RAMCA master key. Rejoining rotates it.",
                    next = "Call board_sync with includeSnapshot=true, roomId, agentId and credential; review pins and tasks before editing."
                }));
            });

        yield return new ToolDefinition(
            Name: "board_invite", Category: "board",
            Description: "Coordinator issues a single-use invitation for a new worker or replacement of an existing agent. Copy the prompt to a new connected chat; it expires automatically.",
            IsReadOnly: false, IsIdempotent: false,
            SideEffects: "Saves one hashed one-use join/rejoin invitation; the code is shown only in this response.",
            Parameters:
            [
                new("roomId", "string", "Room ID.", Required: true),
                new("agentId", "string", "Coordinator ID.", Required: true),
                new("credential", "string", "Coordinator scoped credential.", Required: true),
                new("name", "string", "Human-readable name for the joining chat.", Required: true),
                new("role", "string", "worker or reviewer for new agents; an existing agent's role for replacement.", Default: "worker"),
                new("targetAgentId", "string", "Existing agent to replace, preserving its task identity and history."),
                new("validDays", "integer", "Invitation lifetime in days, 1..30.", Default: 7)
            ],
            Handler: (args, _, _) =>
            {
                var room = Room(args);
                var name = args.RequireString("name");
                var invite = board.CreateInvitation(room, args.RequireString("agentId"),
                    args.RequireString("credential"), args.OptionalString("targetAgentId"),
                    args.OptionalString("role", "worker")!, args.OptionalInt("validDays", 7));
                var prompt = new StringBuilder()
                    .AppendLine($"You are {name}, a {invite.Role} in the RAMCA room '{room.Name}'.")
                    .AppendLine("Your RAMCA MCP connection must be configured and reachable before invoking tools.")
                    .AppendLine($"Workspace: {room.Workspace}")
                    .AppendLine("Stay on main, do not create branches or worktrees, and follow HUMANIFY.")
                    .AppendLine($"Call board_join with room='{room.Id}', name='{name}', inviteCode='{invite.Code}'.")
                    .AppendLine("Save the returned new agentId and scoped credential privately.")
                    .AppendLine("Call board_sync with includeSnapshot=true and read pins, assigned tasks and messages.")
                    .AppendLine("Claim or adopt your specific task before writing. Post progress and evidence to the board.")
                    .AppendLine("An inactive webchat does not receive or act on messages until it reconnects.")
                    .ToString();
                return Task.FromResult(ToolResult.Ok(
                    $"Invitation created for '{name}' (expires {invite.ExpiresAt:yyyy-MM-dd}).",
                    new { roomId = room.Id, invite.AgentId, invite.Role, invite.ExpiresAt,
                        privatePrompt = prompt, note = "Treat this one-use invitation as private; never store it in public project files." }));
            });

        yield return new ToolDefinition(
            Name: "board_sync", Category: "board",
            Description: "Read a bounded page of room messages and current tasks/pins. Cursor and acknowledgment are persistent across chat replacement.",
            IsReadOnly: true, IsIdempotent: true, SideEffects: "None.",
            Parameters:
            [
                new("roomId", "string", "Durable room ID.", Required: true),
                new("agentId", "string", "Durable agent ID.", Required: true),
                new("credential", "string", "Scoped credential returned by board_join.", Required: true),
                new("after", "integer", "Last processed event sequence; 0 starts at the beginning.", Default: 0),
                new("limit", "integer", "Max events in the page, 1..100.", Default: 25),
                new("taskOffset", "integer", "Task pagination offset.", Default: 0),
                new("includeSnapshot", "boolean", "Include task, pin and agent lists. Use true on first join; omit for quick subsequent event polling.", Default: false)
            ],
            Handler: (args, _, _) =>
            {
                var room = Room(args);
                var agent = Agent(args, room);
                var after = Math.Max(0, args.OptionalLong("after", 0));
                var limit = Math.Clamp(args.OptionalInt("limit", 25), 1, 100);
                // The coordinator sees all targeted messages; workers see public and directly targeted posts.
                var (events, hasMore) = board.ReadEvents(room, after, limit,
                    agent.Role.Equals("coordinator", StringComparison.OrdinalIgnoreCase) ? null : agent.Id);
                var next = events.Count == 0 ? after : events[^1].Sequence;
                var includeSnapshot = args.OptionalBool("includeSnapshot", false);
                IReadOnlyList<BoardTask> taskPage = includeSnapshot
                    ? board.ListTasks(room, 41, args.OptionalInt("taskOffset", 0)) : [];
                IReadOnlyList<BoardTask> tasks = taskPage.Take(40).ToArray();
                IReadOnlyList<BoardPin> pinPage = includeSnapshot ? board.ListPins(room, 41) : [];
                IReadOnlyList<BoardPin> pins = pinPage.Take(40).ToArray();
                IReadOnlyList<BoardAgent> members = includeSnapshot ? board.ListAgents(room, 40) : [];
                var acknowledgments = includeSnapshot ? board.ListAcknowledgments(room) :
                    (IReadOnlyDictionary<string, long>)new Dictionary<string, long>();
                var acknowledged = board.GetAcknowledged(room, agent.Id);
                return Task.FromResult(ToolResult.Ok(
                    $"Room '{room.Name}': {events.Count} event(s), {tasks.Count} task(s), {pins.Count} pin(s).",
                    new
                    {
                        room, agent, events, nextCursor = next, hasMore,
                        acknowledgedCursor = acknowledged,
                        tasks, taskOffset = Math.Max(0, args.OptionalInt("taskOffset", 0)),
                        pins, agents = members, acknowledgments, snapshotIncluded = includeSnapshot,
                        taskHasMore = taskPage.Count > 40, pinHasMore = pinPage.Count > 40,
                        note = "Events arrive through active tool calls only. Call board_ack after processing a page."
                    }));
            });

        yield return new ToolDefinition(
            Name: "board_wait", Category: "board",
            Description: "Wait up to 20 seconds for a new room event without polling. Only an active connected agent can call this; dormant webchats remain idle.",
            IsReadOnly: true, IsIdempotent: true, SideEffects: "None.",
            Parameters:
            [
                new("roomId", "string", "Room ID.", Required: true),
                new("agentId", "string", "Agent ID.", Required: true),
                new("credential", "string", "Scoped credential.", Required: true),
                new("after", "integer", "Last event sequence processed.", Default: 0),
                new("waitMs", "integer", "Maximum waiting time in milliseconds, 1..20000.", Default: 15000)
            ],
            Handler: async (args, _, ct) =>
            {
                var room = Room(args);
                var agent = Agent(args, room);
                var after = Math.Max(0, args.OptionalLong("after", 0));
                var waitMs = Math.Clamp(args.OptionalInt("waitMs", 15000), 1, 20_000);
                var visibleTo = agent.Role.Equals("coordinator", StringComparison.OrdinalIgnoreCase) ? null : agent.Id;
                var changed = await board.WaitForEventsAsync(room, after, waitMs, visibleTo, ct).ConfigureAwait(false);
                return ToolResult.Ok(changed ? "New board events are available." : "No new board events.",
                    new { changed, latestCursor = board.LatestSequence(room, visibleTo),
                        next = changed ? "Call board_sync with your last event cursor." : "No action required." });
            });

        yield return new ToolDefinition(
            Name: "board_post", Category: "board",
            Description: "Post a durable message, steering request, blocker, review request, decision or status update. Use a stable requestId to prevent duplicate posts after network loss.",
            IsReadOnly: false, IsIdempotent: true,
            SideEffects: "Appends one durable room event (deduplicated by room, author and requestId).",
            Parameters:
            [
                new("roomId", "string", "Room ID.", Required: true),
                new("agentId", "string", "Author agent ID.", Required: true),
                new("credential", "string", "Scoped agent credential.", Required: true),
                new("kind", "string", "message | steering | blocker | decision | review_request | status.", Default: "message"),
                new("body", "string", "Short durable message, up to 4000 characters.", Required: true),
                new("requestId", "string", "Stable unique ID retained for retry; do not regenerate on uncertain success.", Required: true),
                new("targetAgentId", "string", "Optional direct recipient; coordinator can see all messages.")
            ],
            Handler: (args, _, _) =>
            {
                var room = Room(args);
                var post = board.Post(room, args.RequireString("agentId"), args.RequireString("credential"),
                    args.OptionalString("kind", "message")!, args.RequireString("body"),
                    args.RequireString("requestId"), args.OptionalString("targetAgentId"));
                return Task.FromResult(ToolResult.Ok($"Message posted at sequence {post.Sequence}.", post));
            });

        yield return new ToolDefinition(
            Name: "board_ack", Category: "board",
            Description: "Acknowledge an event sequence after reading it. Queued messages are not considered seen merely because a connection exists.",
            IsReadOnly: false, IsIdempotent: true, SideEffects: "Advances durable recipient acknowledgment cursor.",
            Parameters:
            [
                new("roomId", "string", "Room ID.", Required: true),
                new("agentId", "string", "Agent ID.", Required: true),
                new("credential", "string", "Scoped credential.", Required: true),
                new("sequence", "integer", "Highest event sequence actually processed.", Required: true)
            ],
            Handler: (args, _, _) =>
            {
                var room = Room(args);
                var id = args.RequireString("agentId");
                board.Acknowledge(room, id, args.RequireString("credential"), args.OptionalLong("sequence", -1));
                return Task.FromResult(ToolResult.Ok("Board cursor acknowledged.",
                    new { acknowledgedCursor = board.GetAcknowledged(room, id) }));
            });

        yield return new ToolDefinition(
            Name: "board_task", Category: "board",
            Description: "Create, list, claim, adopt, renew, assign, release, complete, block, reopen or revise durable tasks with revision checks. Expired claims require reconciliation.",
            IsReadOnly: false, IsIdempotent: false,
            SideEffects: "Lists or changes durable task state. No project files are edited.",
            Parameters:
            [
                new("roomId", "string", "Room ID.", Required: true),
                new("agentId", "string", "Agent ID.", Required: true),
                new("credential", "string", "Scoped credential.", Required: true),
                new("action", "string", "list | create | claim | adopt | renew | release | complete | block | reopen | assign | revise.", Required: true),
                new("title", "string", "Task title for create."),
                new("details", "string", "Task acceptance criteria and requirements for create."),
                new("workArea", "string", "Advisory file/subsystem scope for create."),
                new("requestId", "string", "Stable unique request ID for task creation. Retain on retry."),
                new("taskId", "string", "Task ID for a mutation."),
                new("expectedRevision", "integer", "Revision returned by board_sync; required for mutations."),
                new("evidence", "string", "Concise commit/test/blocker evidence."),
                new("assigneeId", "string", "Target agent ID for coordinator assignment."),
                new("leaseMinutes", "integer", "Claim duration, 1..60 minutes.", Default: 15),
                new("offset", "integer", "Task list offset.", Default: 0)
            ],
            Handler: (args, _, _) =>
            {
                var room = Room(args);
                var id = args.RequireString("agentId");
                var secret = args.RequireString("credential");
                Agent(args, room);
                var action = args.RequireString("action");
                if (action == "list")
                {
                    var page = board.ListTasks(room, 41, args.OptionalInt("offset", 0));
                    var tasks = page.Take(40).ToArray();
                    return Task.FromResult(ToolResult.Ok($"Retrieved {tasks.Length} task(s).",
                        new { tasks, offset = args.OptionalInt("offset", 0), pageSize = 40,
                            hasMore = page.Count > 40 }));
                }
                BoardTask task;
                if (action == "create")
                    task = board.CreateTask(room, id, secret,
                        args.RequireString("title"), args.RequireString("details"), args.OptionalString("workArea"),
                        args.RequireString("requestId"));
                else
                {
                    var revision = args.OptionalLong("expectedRevision", 0);
                    task = board.ChangeTask(room, id, secret, args.RequireString("taskId"), action,
                        revision, args.OptionalString("evidence"), args.OptionalString("assigneeId"),
                        args.OptionalInt("leaseMinutes", 15),
                        args.OptionalString("title"), args.OptionalString("details"));
                }
                return Task.FromResult(ToolResult.Ok($"Task '{task.Title}' is {task.Status} (revision {task.Revision}).", task));
            });

        yield return new ToolDefinition(
            Name: "board_pin", Category: "board",
            Description: "List or upsert persistent room decisions and onboarding notes with revision checks.",
            IsReadOnly: false, IsIdempotent: false,
            SideEffects: "May update a durable pin. A list action is read-only.",
            Parameters:
            [
                new("roomId", "string", "Room ID.", Required: true),
                new("agentId", "string", "Agent ID.", Required: true),
                new("credential", "string", "Scoped credential.", Required: true),
                new("action", "string", "list, save or delete.", Required: true),
                new("key", "string", "Stable pin key for save."),
                new("title", "string", "Pin title for save."),
                new("body", "string", "Pin content for save."),
                new("expectedRevision", "integer", "Existing revision when replacing a pin."),
                new("offset", "integer", "List offset for older pins.", Default: 0),
                new("limit", "integer", "List page size, 1..100.", Default: 25)
            ],
            Handler: (args, _, _) =>
            {
                var room = Room(args);
                Agent(args, room);
                if (args.RequireString("action") == "list")
                {
                    var offset = Math.Max(0, args.OptionalInt("offset", 0));
                    var limit = Math.Clamp(args.OptionalInt("limit", 25), 1, 99);
                    var page = board.ListPins(room, limit + 1, offset);
                    return Task.FromResult(ToolResult.Ok("Room pins.",
                        new { pins = page.Take(limit).ToArray(), offset, hasMore = page.Count > limit }));
                }
                if (args.RequireString("action") == "delete")
                {
                    board.DeletePin(room, args.RequireString("agentId"), args.RequireString("credential"),
                        args.RequireString("key"), args.OptionalLong("expectedRevision", 0));
                    return Task.FromResult(ToolResult.Ok("Pin removed.", new { key = args.RequireString("key") }));
                }
                if (args.RequireString("action") != "save")
                    return Task.FromResult(ToolResult.Fail("invalid_arguments", "Pin action must be list, save or delete."));
                long? revision = args.TryGetPropertyValue("expectedRevision", out var r) && r is not null
                    ? args.OptionalLong("expectedRevision", 0) : null;
                var pin = board.SavePin(room, args.RequireString("agentId"), args.RequireString("credential"),
                    args.RequireString("key"), args.RequireString("title"), args.RequireString("body"), revision);
                return Task.FromResult(ToolResult.Ok($"Pin '{pin.Key}' saved (revision {pin.Revision}).", pin));
            });

        yield return new ToolDefinition(
            Name: "board_handoff", Category: "board",
            Description: "Generate a compact durable task/pin/status handoff to copy into a fresh compatible chat. Does not restore model context or silently connect the new chat.",
            IsReadOnly: true, IsIdempotent: true, SideEffects: "None.",
            Parameters:
            [
                new("roomId", "string", "Room ID.", Required: true),
                new("agentId", "string", "Agent identity to continue.", Required: true),
                new("credential", "string", "Scoped credential.", Required: true),
                new("includeResumeCredential", "boolean", "Embed the scoped agent credential in the generated PRIVATE handoff prompt.", Default: false)
            ],
            Handler: (args, _, _) =>
            {
                var room = Room(args);
                var agent = Agent(args, room);
                var taskPage = board.ListTasks(room, 41);
                var pinPage = board.ListPins(room, 26);
                var tasks = taskPage.Take(40).ToArray();
                var pins = pinPage.Take(25).ToArray();
                var acknowledged = board.GetAcknowledged(room, agent.Id);
                var executions = database.ListWorkspaceExecutionSummaries(room.Workspace, 12);
                var sb = new StringBuilder();
                sb.AppendLine($"You are continuing RAMCA agent '{agent.Name}' ({agent.Role}) in room '{room.Name}'.");
                sb.AppendLine("First connect your own compatible RAMCA MCP tools. Pasting this prompt alone does not connect tools.");
                sb.AppendLine($"Workspace: {room.Workspace}");
                sb.AppendLine("Git policy: work directly on main; never create branches/worktrees without owner authorization.");
                sb.AppendLine("Commit coherent changes separately with descriptive HUMANIFY-style messages.");
                sb.AppendLine($"Room ID: {room.Id}");
                sb.AppendLine($"Agent ID: {agent.Id}");
                if (args.OptionalBool("includeResumeCredential", false))
                {
                    sb.AppendLine($"Scoped resume credential (private): {args.RequireString("credential")}");
                    sb.AppendLine("Call board_join with room, agentId and credential to reattach; save the newly rotated credential.");
                }
                else sb.AppendLine("Retrieve the scoped resume credential privately from the previous agent; it is deliberately excluded here.");
                sb.AppendLine($"Previously acknowledged board event: {acknowledged}. After rejoining, call board_sync to read new events.");
                sb.AppendLine("Pins:");
                foreach (var pin in pins)
                    sb.AppendLine($"- {pin.Key}: {pin.Title} - {pin.Body}");
                if (pinPage.Count > 25) sb.AppendLine("More pins exist. Call board_pin list to inspect them.");
                sb.AppendLine("Tasks:");
                foreach (var task in tasks)
                    sb.AppendLine($"- {task.Id} [{task.Status}] ({task.OwnerId ?? "unassigned"}) {task.Title}; revision={task.Revision}; area={task.WorkArea ?? "unspecified"}; evidence={task.Evidence ?? "none"}");
                if (taskPage.Count > 40) sb.AppendLine("More tasks exist. Call board_task list with offset=40 to continue.");
                var checkpoints = database.ListWorkspaceGitCheckpoints(room.Workspace, 3);
                if (checkpoints.Count > 0)
                {
                    sb.AppendLine("Last recorded Git checkpoints (verify HEAD yourself):");
                    foreach (var cp in checkpoints)
                        sb.AppendLine($"- {cp.Branch}@{cp.CommitSha}: {cp.Label}");
                }
                sb.AppendLine("Recent jobs and workflows (recorded status, check live state):");
                foreach (var execution in executions)
                    sb.AppendLine($"- {execution.Kind} {execution.Id} [{execution.Status}]: {execution.Name}" +
                        (execution.LogExecId is null ? "" : $"; logExecId={execution.LogExecId}"));
                if (executions.Any(e => e.Status is "running" or "queued" or "interrupted_restart"))
                    sb.AppendLine("Inspect pending/interrupted jobs before starting new mutations. Never blindly retry them.");
                sb.AppendLine("Never claim a tool/test/commit succeeded without verifying it. Inspect the current Git state and task claim before writing.");
                var prompt = sb.ToString();
                if (prompt.Length > 16_000)
                    return Task.FromResult(ToolResult.Fail("handoff_too_large",
                        "Handoff exceeded 16,000 characters; shorten pins/task descriptions before exporting."));
                return Task.FromResult(ToolResult.Ok("Copy-ready agent handoff created from durable RAMCA records.",
                    new { prompt, roomId = room.Id, agentId = agent.Id,
                        acknowledgedCursor = acknowledged, pinCount = pins.Length, taskCount = tasks.Length,
                        pinsTruncated = pinPage.Count > 25, tasksTruncated = taskPage.Count > 40,
                        jobCountIncluded = executions.Count }));
            });

        yield return new ToolDefinition(
            Name: "board_work", Category: "board",
            Description: "Run one agent task action under its current lease and RAMCA workspace write lock. Use execute_once around board_work for edits/commits if a response may be lost.",
            IsReadOnly: false, IsIdempotent: false,
            SideEffects: "Can edit, build, run commands or create a scoped Git commit, using the same host tool policies as direct tools.",
            Parameters:
            [
                new("roomId", "string", "Room ID.", Required: true),
                new("agentId", "string", "Current agent ID.", Required: true),
                new("credential", "string", "Current scoped agent credential.", Required: true),
                new("taskId", "string", "Currently claimed task ID.", Required: true),
                new("tool", "string", "One engineering tool to invoke under task ownership.", Required: true),
                new("arguments", "object", "Ordinary arguments for the selected engineering tool.", Required: true)
            ],
            Handler: async (args, ctx, ct) =>
            {
                var room = Room(args);
                var agentId = args.RequireString("agentId");
                var credential = args.RequireString("credential");
                var taskId = args.RequireString("taskId");
                var toolName = args.RequireString("tool");

                // Keep this list small. Arbitrary workflows, pushes and branch changes
                // should never be smuggled through a task claim.
                var allowed = toolName is
                    "create_file" or "replace_range" or "apply_patch" or "rename_file" or
                    "delete_file" or "revert_edit" or "revert_rename" or "dotnet_build" or "dotnet_test" or
                    "msbuild_run" or "run_command" or "git_commit";
                if (!allowed || registry.Find(toolName) is null)
                    return ToolResult.Fail("board_tool_denied",
                        "This tool is not supported by board_work. Use direct read-only tools for inspection; branch changes, pushes, nested jobs and workflow launches are not allowed.");

                if (args["arguments"] is not JsonObject inner)
                    return ToolResult.Fail("invalid_arguments", "arguments must be an object.");

                var needsFileVersion = toolName is "replace_range" or "delete_file" or "rename_file" ||
                    toolName == "create_file" && inner.OptionalBool("overwrite", false);
                if (needsFileVersion && string.IsNullOrWhiteSpace(inner.OptionalString("expectedSha256")))
                    return ToolResult.Fail("expected_version_required",
                        "Board-controlled edits require expectedSha256 from a recent read. Refusing to overwrite changes made by another agent or editor.");
                if (toolName == "apply_patch" &&
                    string.IsNullOrWhiteSpace(inner.OptionalString("expectedSha256")) &&
                    (inner["expectedSha256Map"] is not JsonObject map || map.Count == 0))
                    return ToolResult.Fail("expected_version_required",
                        "Board-controlled patches require expectedSha256 or non-empty expectedSha256Map. Use create_file for a new file.");

                // Claim transitions take this same workspace lock. A task cannot be
                // reassigned while one of its board_work operations is in progress.
                using var writeLock = await guard.AcquireWriteLockAsync(room.Workspace, ct).ConfigureAwait(false);
                var task = board.RequireActiveClaim(room, agentId, credential, taskId);

                var gitHead = Path.Combine(room.Workspace, ".git", "HEAD");
                if (!File.Exists(gitHead) ||
                    !string.Equals(File.ReadAllText(gitHead).Trim(),
                        "ref: refs/heads/main", StringComparison.Ordinal))
                    return ToolResult.Fail("main_only", "Agent board writes require an ordinary Git checkout on main.");

                if (toolName == "git_commit")
                {
                    if (inner.OptionalBool("stageAll", false) || inner.OptionalBool("allowPreviouslyStaged", false) ||
                        inner.OptionalStringList("paths").Count == 0)
                        return ToolResult.Fail("commit_scope_required",
                            "Agent commits require explicit file paths and cannot include unrelated staged work.");
                }

                if (!string.IsNullOrWhiteSpace(task.WorkArea))
                {
                    var area = guard.ResolvePath(task.WorkArea, forWrite: true);
                    string[] pathFields = toolName switch
                    {
                        "create_file" or "replace_range" or "delete_file" => ["path"],
                        "rename_file" => ["sourcePath", "destinationPath"],
                        "git_commit" => [],
                        _ => []
                    };
                    foreach (var field in pathFields)
                    {
                        var full = guard.ResolvePath(inner.RequireString(field), forWrite: true);
                        if (!WorkspaceGuard.IsEqualOrInside(full, area))
                            return ToolResult.Fail("outside_task_area",
                                $"'{field}' is outside the task's work area '{task.WorkArea}'.");
                    }
                    if (toolName == "git_commit")
                    {
                        foreach (var path in inner.OptionalStringList("paths"))
                        {
                            var full = guard.ResolvePath(path, forWrite: true);
                            if (!WorkspaceGuard.IsEqualOrInside(full, area))
                                return ToolResult.Fail("outside_task_area",
                                    $"Commit path '{path}' is outside the task's work area.");
                        }
                    }
                    if (toolName is "apply_patch" or "revert_edit" or "revert_rename" or "run_command")
                        return ToolResult.Fail("unscoped_tool",
                            "This operation cannot be confined to a task work area. Use narrowly scoped edits or a task without a restricted work area.");
                }

                // Inner tools must not reacquire the same non-reentrant write lock.
                var result = await registry.InvokeAsync(toolName, (JsonObject)inner.DeepClone(),
                    new ToolExecutionContext(ctx.SessionId, ctx.JobId, ctx.ReportProgress,
                        SkipWriteLock: true, WorkspaceRoot: room.Workspace), ct).ConfigureAwait(false);

                // The action already ran. A failed board receipt must never invite a blind retry.
                try
                {
                    var summary = result.Summary;
                    if (summary.Length > 1200) summary = summary[..1200];
                    board.Post(room, agentId, credential,
                        result.Success ? "status" : "blocker",
                        $"Task {taskId}: {toolName} {(result.Success ? "completed" : "failed")}. {summary}",
                        "work_" + Guid.NewGuid().ToString("N"));
                }
                catch (Exception ex)
                {
                    return ToolResult.Fail("board_receipt_missing",
                        $"The tool already ran, but its board report could not be saved ({ex.GetType().Name}). Check the actual result before retrying.",
                        new { executedResult = result }, result.ExecutionId);
                }
                return result;
            });
    }
}
