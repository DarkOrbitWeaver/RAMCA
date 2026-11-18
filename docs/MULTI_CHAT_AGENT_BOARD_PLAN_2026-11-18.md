# RAMCA — multi-chat agent coordination design

**Status:** engineering design, not an implemented feature.  
**Owner intent:** personal Windows 11 coding companion for the owner and one friend. Work directly on `main`, with isolated, descriptive commits. Do not create branches, worktrees, or pull requests unless explicitly instructed.  
**Date:** 2026-11-18.  
**Integrates with:** `StateDatabase`, `ToolRegistry`, `WorkbenchHost`, `SupervisorTui`, `JobManager`, `WorkflowEngine`, `execute_once`, and the audited engineering ledger.

## 1. What this solves

The owner should be able to open one ChatGPT chat as a coordinator, open multiple independent chats as coding agents, and have all of them share an inspectable task board, durable chatboard, and pinned project knowledge inside the same running RAMCA instance. The coordinator writes a compact invitation prompt for each new chat. A worker joins a specified project room, reads assigned tasks and current checkpoints, claims a task, works, and posts evidence and progress. The coordinator can delegate, review, steer, and reconnect after quota exhaustion. Any newly opened compatible chat can resume an existing role without relying on its original conversation text.

**Hard truth:** RAMCA can persist messages, assignments, files, logs and handoffs; it cannot force an inactive web ChatGPT conversation to think, send messages, or poll tools. Agents only receive new chatboard content while their client is actually running and invoking the relevant tools, or when an independently supported agent runtime is active. "Message sent to agent" means "durably queued for that agent", not "read by the model." Do not claim hidden coordination, continuous AI inference, or automatic model session restoration.

**Prerequisite:** this feature must not outrun the current data-integrity and Windows verification blockers. Persisting a task is easy; safely letting several agents edit one `main` working tree is the difficult part. Start collaboration in read-only/coordination mode and enable concurrent writes only after the existing patch, Git, session pinning, recovery and concurrency gates pass.

## 2. The owner's intended experience

1. Launch RAMCA, choose the trusted VESPER workspace, and open the compact **Agents / Board** view. No web dashboard, service, account registration, paid provider or bundled inference model.
2. Create a room named `VESPER` with one coordinator role and optional worker roles (e.g. engine, Chrome extension, UI, verification). Roles are display/coordination conventions, not a commercial permission system.
3. RAMCA produces a **Join prompt** with the room ID, agent role, expected first tools, branch policy and a short board checksum. No API key is embedded; each ChatGPT client's RAMCA tool connection must already be configured.
4. A worker connects, invokes `board_join`, then `board_sync`, claims a task, and reports incremental evidence. Coordinator sees messages, task ownership, unread mentions, blockers, commits, checks and pending decisions.
5. Coordinator can assign tasks, add a durable pin, leave direct instructions for a specific agent, ask for a review, or reassign an abandoned task. All are visible as auditable board events.
6. If a chat reaches its usage limit or is closed, another compatible chat receives a fresh **Resume prompt** and calls `board_join` with the existing stable agent identity plus a local handoff code. It reads a snapshot plus changes since the saved cursor, not a giant transcript.
7. Owner can shut RAMCA down. Chats disconnect and all owned processes exit as today. On restart, room, messages, tasks, pins, claims, reports and checkpoints still exist in the portable SQLite database; running jobs follow RAMCA's existing interrupted-state semantics.

## 3. Minimal durable data model

Use the existing portable `data/state.db` (SQLite WAL; no second DB or service). Schema migrations must be idempotent and covered by reopen/upgrade tests. Use UTC timestamps and monotonically increasing sequence numbers; bind every row to the canonical workspace identity and room. All mutations execute as short SQLite transactions, with explicit revision checks.

- **board_rooms**: room ID, pinned workspace identity, project label, created/updated time, archived flag; default branch policy `main-only`.
- **board_agents**: stable agent ID, room ID, human-readable role/name, active attachment generation, last acknowledged cursor, last-seen time, last handoff reference, state. Stable identity is chosen at join, *not* inferred from ChatGPT web session IDs.
- **board_events**: global increasing event sequence, room, author, event kind, target agent (optional), compact text/body, related task/pin/artifact IDs, creation time. Event kinds: message, mention, assignment, status, steering, note, review_request, decision, system.
- **board_tasks**: durable task ID, room, title, concrete acceptance criteria, priority, status, assignee, claim generation, lease deadline, optimistic revision, work area/path hints, block reason, base commit SHA, last evidence reference, created/updated. Task dependencies can be a bounded JSON array initially, validated against cycles, instead of a new graph service.
- **board_pins**: room, stable key, title, concise body/linked workspace path, author, revision, updated time. Examples: architecture decisions, no-work-loss rules, current release blockers, test command, API contract, contact point.
- **board_inbox_cursors**: agent/room read and acknowledged positions; an unread count is derived from persisted events, not maintained in unbounded RAM.
- **board_artifacts** (only if needed after v1): metadata handles to existing logs, job results, Git commits, diff edit IDs and source paths. Do not duplicate full log bodies or secrets into chatboard messages.

Avoid persistent sockets per worker and an in-memory copy of complete board history. Back every listing with pagination and indexes on room + sequence, assignee + status, and lease deadlines. Enforce bounded message length, response page sizes and rate limits. Retention must never erase active tasks, pinned decisions, unacknowledged steering, or the last recoverable handoff; older ordinary messages can be exported/compacted only after independent verification.

## 4. Small MCP / HTTP / CLI tool surface

Implement in a dedicated `Tools/BoardTools.cs` and `Storage/BoardStore.cs` (or a carefully scoped partial of the existing storage class). Register through the same `ToolRegistry` so MCP, HTTP, CLI and workflows agree. Keep tool descriptions short to control discovery/token overhead.

| Tool | Behavior |
|---|---|
| `board_join` | Create or resume stable agent in a room; return agent ID, attachment generation, branch/workspace sentinel, unread summary and lightweight boot instructions. A resume code is for accidental identity collision prevention, not a replacement for the existing RAMCA transport credential. |
| `board_sync` | Read concise room snapshot + event page strictly after a cursor; filter by room, task and mentions; return `nextCursor`, `hasMore` and truthful truncation flags. |
| `board_post` | Append a message, mention, steering instruction, decision, blocker or review request; return event sequence. Idempotency key required for retry after ambiguous network failure. |
| `board_task` | Create, list, assign, claim, renew, update, release, mark blocked or finish. Mutation uses task revision and atomic lease/claim generation checks. Listing is paged. |
| `board_pin` | List, create, update or remove a pin with expected revision. Include a concise pin index in handoffs. |
| `board_ack` | Acknowledge receipt of messages or a specific steering event with a monotonic cursor. Distinguish queued, delivered-by-tool and acknowledged-by-agent. |
| `board_handoff` | Generate bounded, human-readable join/resume instructions from stored tasks, pins, verified job and Git state, decision blockers and board cursor. No secrets or private master key in prompts. |

Optional *later*: `board_wait` can wait on a bounded event notification without polling, using the same race-free waiter pattern as jobs; do not rely on universal MCP push/notification support.

All write tools are registered with honest `IsReadOnly`, `IsIdempotent` and `SideEffects` metadata. Distinguish immutable event append (retry keyed) from CAS task/pin updates. Board changes are coordinated data, not file-editing operations, and must not inadvertently acquire a workspace-wide code-write lock.

## 5. Cross-chat delivery, identity and reattachment

- Each ChatGPT conversation is its own active client. RAMCA cannot identify a user's ChatGPT chat ID unless the client supplies one; never assume a hidden ChatGPT API.
- **Coordinator:** any connected chat whose instructions include the coordinator role can supervise, but the board cannot guarantee its continued presence. If the leader is offline, workers can finish owned work and queue requests; they must not fabricate approvals or infer unreceived steering.
- **Messages:** durable event append and incrementing cursor. A targeted message is *queued* until the target polls and acknowledges it. If a worker is inactive, it does not silently receive instructions.
- **Resume:** a generated resume prompt contains room ID, stable agent ID, last acknowledged cursor and human-facing handoff identifier. RAMCA verifies the supplied resume code (hashed in storage if used) and increments attachment generation; stale attachments cannot update task ownership. Do not include transport secret keys in copy-ready prompts.
- **Handoff:** show task objective and constraints, current status, owned paths, last verified commit, active/unknown jobs, exact failing tests, relevant pins, and next safe actions. Source is durable evidence, never a made-up reconstruction of model thoughts.
- **No hidden inference daemon:** background tasks may run deterministic `workflow_run` steps while RAMCA is running, but an idle webchat does not perform new reasoning. Fully autonomous asynchronous AI would require a separate supported inference/agent runtime, explicitly opted into later.

## 6. Main-only concurrency contract

All edits happen on the owner's existing **main** checkout; no branches, worktrees or temporary PRs by default. In a shared checkout, Git index and working tree are global, so guard them rather than trusting prompt discipline alone.

1. Every worker must claim an explicit task before making project edits. The task records a workspace/branch/HEAD sentinel and declared work-area hints.
2. Lease claims are atomic (SQLite IMMEDIATE/compare-and-swap). Use a renewable lease and a generation number. A lease expiring does *not* imply that a partially completed edit is safe to overwrite; expiration moves the task into **needs reconciliation**, not automatic reassignment.
3. Initially allocate non-overlapping files/subsystems and serialize all mutations through the existing workspace write lock. Introduce file-scoped concurrency only after proving it cannot conflict with patch transactions, builds, Git index or the human editor.
4. Recheck file SHA and workspace/branch identity immediately before edits. If either changed, stop and post a blocker with a diff/inspection reference; never silently overwrite a human save or another agent's work.
5. Serialize **stage and commit** operations. Stage only owned explicit paths. Preserve preexisting staged content and refuse ambiguous shared-index commits. Do not use `git add -A`, `git commit -a`, reset, checkout, force-clean or branch creation for multi-chat workflows.
6. Name commits by coherent changes and link each commit SHA to its originating task. Prefer a separate reviewed commit for each worker deliverable; do not mix unrelated fixes.
7. External push/publish remains separately controlled by the owner's existing policy. The owner may authorize pushes, but board role labels must not bypass RAMCA's host policy.
8. A second agent can review another agent's result without seizing the same write lease. Coordinator can request a deliberate ownership transfer only after status, diff and in-flight tool operations are inspected.

This is a *file and operation arbitration problem*, not a way to create isolated Git histories. Parallel reading, audits and disjoint planning can scale before parallel writes are enabled.

## 7. TUI design and performance

Add one compact **Board** top-level tab to `SupervisorTui`, reusing its existing event-driven render loop and paginated SQLite model. Do not add WinForms, web pages, persistent background services, timers or external UI libraries.

- Header: project room, connected/recent agents, queued/unread mentions, active tasks, blocked tasks.
- Modes within the tab: **Chat**, **Tasks**, **Pins**. Navigation by a handful of discoverable keyboard shortcuts; current selection can show details in the existing right/details area.
- Chat lines show author, short timestamp, target and status (queued / acknowledged). Avoid fake "online" status: show "last seen" derived from real tool activity.
- Tasks show owner, concise progress/status, blockers, last checked commit/tests; leader can view and copy task assignment prompts.
- Pins display decisions and durable onboarding context; include "copy join prompt" and "copy resume prompt".
- Use event-based wakeups; render only visible lines, keep bounded in-memory pages, cache by sequence/revision and invalidate on actual board writes.
- Board stays useful over ordinary terminal sizes, headless CLI and monochrome mode; no animation/flicker or unrelated dashboard polish.

## 8. Delivery stages and release gates

**Stage 0 — prerequisites / no-loss P0 (blocking):**
- Fix and reproduce the large-file delete/restore regression.
- Validate patch atomicity, preconditions, correct rollback and crash recovery; preserve external editor saves.
- Prove job/session/workspace pinning, idempotent `execute_once`, Git scope and status parsing.
- Establish a clean, real Windows Release build and full test suite. Do not enable shared autonomous writes before this passes.

**Stage 1 — durable coordination only (can land independently after baseline):**
- Add SQLite migrations, repository methods, compact board tool contracts and a disposable-workspace test harness.
- Implement join, sync, post, pins and task CRUD with cursor/revision semantics.
- Tests: durable restart, lost response/idempotency, stale revision, independent room isolation, malformed input, bounded page sizes and no secret echo.

**Stage 2 — task ownership and handoff:**
- Atomic lease/renew/reconcile; declared work-area reservation; branch/workspace sentinel; agent inbox acknowledgments.
- Generate join/resume/handoff prompts using actual state and correct source references.
- Tests: two simultaneous claims, stale agent after reconnect, abandoned task, lost event page, stopped host, changed branch, concurrent human editor.

**Stage 3 — terminal board:**
- Event-driven Chat/Tasks/Pins, lazy paging, copy prompts, agent acknowledgment/unread state and accurate failure display.
- Tests: terminal resize and keyboard behavior, 10k+ board events without accumulating RAM, idle CPU and disposal.

**Stage 4 — controlled multi-chat VESPER pilot:**
- One coordinator chat + two worker chats + one reviewer; all connected to a disposable clone first, then owner-approved VESPER files.
- Each chat can rejoin after a new conversation and retrieve the exact outstanding task, latest commit and test evidence.
- Confirm direct-to-main commits are nonoverlapping and staging never captures another agent's changes.
- Kill RAMCA, disconnect a worker during edit, lose an MCP response, reconnect another chat and verify no duplicate write/commit or lost steering.
- Compare model-facing payload size, tool round trips, wall-clock task completion, RAM and CPU against sequential work. Keep the feature only if it is demonstrably useful.

**Stage 5 — optional later conveniences:**
- More expressive task dependencies, review-request queue, selective incident export, richer diff references, condition-based notifications. Only add when justified by actual owner usage.

## 9. HUMANIFY and personal-maintenance policy

The repository audit/maintainability pass (previously called P2 in the owner's conversation) must use the owner's **HUMANIFY skill** when that skill is accessible: load/read the actual skill before applying its guidance. Do not pretend the skill ran when it was not available. Preserve behavior, public tool schemas and tests; remove redundant abstractions and noise only with clear reason and diff review. Never optimize for enterprise SaaS, multi-tenant accounts, business onboarding, paid-model integration or generalized plugin ecosystems. Optimize for this owner's actual Windows workflow, response efficiency, error recoverability and low idle footprint. Keep host authentication, workspace integrity and secret handling because they protect the owner's files, not for commercial compliance.

## 10. Acceptance checklist

- [ ] Existing critical recovery failures fixed and independently tested on Windows.
- [ ] Real independent ChatGPT clients can reach RAMCA and invoke the new tools in the owner's account; document actual client limits.
- [ ] Two separate chats exchange board messages by posting/polling with strict order and explicit acknowledgment.
- [ ] A chat can disappear indefinitely without losing tasks, pins, evidence or a targeted steering instruction.
- [ ] Fresh chat can resume an agent identity through a bounded handoff; stale client cannot silently overwrite its successor's claim.
- [ ] Task claim collision has one winner; expired claim is reconciled rather than blindly transferred.
- [ ] No out-of-workspace changes or accidental second branch/worktree.
- [ ] No staged-file theft or mixed commits with two agents working directly on `main`.
- [ ] Logs, jobs, file diffs and test results are linked by identifiers, not pasted in full into SQLite board messages.
- [ ] Paging, offline board history and TUI work after 10,000 messages and repeated restart; memory and idle CPU remain bounded.
- [ ] Host shutdown kills owned processes/tunnel; persistent board remains usable at next start.
- [ ] Security and recovery documentation states actual guarantees and limits without hype.
- [ ] Every shipped milestone has distinct descriptive `main` commits and tests; never mark a planning document as implemented.

## 11. Explicit external blocker

GitHub's remote `feature/one-click-chatgpt-tunnel` branch was verified on 2026-11-18 to be 58 commits behind `main` with zero unique commits (ahead_by=0). Owner explicitly authorized removing it and retaining only `main`. Branch deletion remains pending because the available GitHub connector does not expose delete-ref and the RAMCA tunnel was disconnected at this checkpoint. When local Git access is available, verify `main` and the ahead/behind result again, then execute `git push origin --delete feature/one-click-chatgpt-tunnel` once; verify remote branch list contains only `main`. Do not create a replacement branch.
