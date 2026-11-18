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
        StateDatabase database)
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
                new("role", "string", "Agent role, such as worker, reviewer or coordinator.", Required: true),
                new("agentId", "string", "Existing agentId to resume (omit for a new identity)."),
                new("credential", "string", "Existing scoped agent credential required when resuming.")
            ],
            Handler: (args, _, _) =>
            {
                var workspace = guard.RequireActiveWorkspace();
                var identifier = args.RequireString("room");
                var room = identifier.StartsWith("room_", StringComparison.Ordinal)
                    ? board.RequireRoom(identifier, workspace)
                    : board.GetOrCreateRoom(identifier, workspace);
                var (agent, secret) = board.Join(room, args.RequireString("name"),
                    args.RequireString("role"), args.OptionalString("agentId"), args.OptionalString("credential"));
                return Task.FromResult(ToolResult.Ok($"Joined room '{room.Name}' as {agent.Name}.", new
                {
                    room, agent,
                    credential = secret,
                    warning = "Save this scoped credential privately for board calls and future reattachment. It is not the RAMCA master key. Rejoining rotates it.",
                    next = "Call board_sync with roomId, agentId and credential; review pinned decisions and task claims before editing."
                }));
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
                new("taskOffset", "integer", "Task pagination offset.", Default: 0)
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
                var tasks = board.ListTasks(room, 40, args.OptionalInt("taskOffset", 0));
                var pins = board.ListPins(room, 40);
                var members = board.ListAgents(room, 40);
                var acknowledged = board.GetAcknowledged(room, agent.Id);
                return Task.FromResult(ToolResult.Ok(
                    $"Room '{room.Name}': {events.Count} event(s), {tasks.Count} task(s), {pins.Count} pin(s).",
                    new
                    {
                        room, agent, events, nextCursor = next, hasMore,
                        acknowledgedCursor = acknowledged,
                        tasks, taskOffset = Math.Max(0, args.OptionalInt("taskOffset", 0)),
                        pins, agents = members,
                        note = "Events are delivered by polling, not pushed into dormant chats. Call board_ack after processing a page."
                    }));
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
            Description: "Create, list, claim, adopt, renew, assign, release, complete, block or reopen durable tasks with revision checks. Expired claims require reconciliation.",
            IsReadOnly: false, IsIdempotent: false,
            SideEffects: "Lists or changes durable task state. No project files are edited.",
            Parameters:
            [
                new("roomId", "string", "Room ID.", Required: true),
                new("agentId", "string", "Agent ID.", Required: true),
                new("credential", "string", "Scoped credential.", Required: true),
                new("action", "string", "list | create | claim | adopt | renew | release | complete | block | reopen | assign.", Required: true),
                new("title", "string", "Task title for create."),
                new("details", "string", "Task acceptance criteria and requirements for create."),
                new("workArea", "string", "Advisory file/subsystem scope for create."),
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
                    var tasks = board.ListTasks(room, 40, args.OptionalInt("offset", 0));
                    return Task.FromResult(ToolResult.Ok($"Retrieved {tasks.Count} task(s).",
                        new { tasks, offset = args.OptionalInt("offset", 0), pageSize = 40 }));
                }
                BoardTask task;
                if (action == "create")
                    task = board.CreateTask(room, id, secret,
                        args.RequireString("title"), args.RequireString("details"), args.OptionalString("workArea"));
                else
                {
                    var revision = args.OptionalLong("expectedRevision", 0);
                    task = board.ChangeTask(room, id, secret, args.RequireString("taskId"), action,
                        revision, args.OptionalString("evidence"), args.OptionalString("assigneeId"),
                        args.OptionalInt("leaseMinutes", 15));
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
                new("action", "string", "list or save.", Required: true),
                new("key", "string", "Stable pin key for save."),
                new("title", "string", "Pin title for save."),
                new("body", "string", "Pin content for save."),
                new("expectedRevision", "integer", "Existing revision when replacing a pin.")
            ],
            Handler: (args, _, _) =>
            {
                var room = Room(args);
                Agent(args, room);
                if (args.RequireString("action") == "list")
                    return Task.FromResult(ToolResult.Ok("Room pins.",
                        new { pins = board.ListPins(room) }));
                if (args.RequireString("action") != "save")
                    return Task.FromResult(ToolResult.Fail("invalid_arguments", "Pin action must be list or save."));
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
                var tasks = board.ListTasks(room, 40);
                var pins = board.ListPins(room, 25);
                var acknowledged = board.GetAcknowledged(room, agent.Id);
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
                    sb.AppendLine($"- {pin.Key}: {pin.Title} — {pin.Body}");
                sb.AppendLine("Tasks:");
                foreach (var task in tasks)
                    sb.AppendLine($"- {task.Id} [{task.Status}] ({task.OwnerId ?? "unassigned"}) {task.Title}; revision={task.Revision}; area={task.WorkArea ?? "unspecified"}; evidence={task.Evidence ?? "none"}");
                var checkpoints = database.ListGitCheckpoints(limit: 3);
                if (checkpoints.Count > 0)
                {
                    sb.AppendLine("Last recorded Git checkpoints (verify HEAD yourself):");
                    foreach (var cp in checkpoints)
                        sb.AppendLine($"- {cp.Branch}@{cp.CommitSha}: {cp.Label}");
                }
                sb.AppendLine("Never claim a tool/test/commit succeeded without verifying it. Inspect the current Git state and task claim before writing.");
                var prompt = sb.ToString();
                if (prompt.Length > 16_000)
                    return Task.FromResult(ToolResult.Fail("handoff_too_large",
                        "Handoff exceeded 16,000 characters; shorten pins/task descriptions before exporting."));
                return Task.FromResult(ToolResult.Ok("Copy-ready agent handoff created from durable RAMCA records.",
                    new { prompt, roomId = room.Id, agentId = agent.Id,
                        acknowledgedCursor = acknowledged, pinCount = pins.Count, taskCount = tasks.Count }));
            });
    }
}
