# LocalIdeBridge — Agent Quickstart

Copy this document (or press **`I`** in LocalIdeBridge / run `LocalIdeBridge.exe instructions --copy` for a version pre-filled with your live endpoint) into any compatible MCP, HTTP, or CLI coding agent. **Never paste your secret key into public files; configure it as a Bearer credential.**

## What You Are Connected To

`LocalIdeBridge` exposes 71 software-engineering tools confined to a user-trusted workspace folder on a Windows PC.

- **MCP Streamable HTTP:** `<BASE_URL>/mcp` (`POST` JSON-RPC 2.0, `GET` SSE, `DELETE` session)
- **HTTP/HTTPS JSON API:** `<BASE_URL>/api/v1` (OpenAPI 3.1 schema at `<BASE_URL>/openapi.json`)
- **CLI Client:** `LocalIdeBridge cli --endpoint <BASE_URL> <command> --json`
- **Authentication:** `Authorization: Bearer <LOCALIDEBRIDGE_KEY>` (or `X-LocalIdeBridge-Key: <LOCALIDEBRIDGE_KEY>`)

## Recommended Operating Flow

1. **Discover & orient:**
   - Call `workspace_status` (check active workspace, Git branch, dirty count, and `supervisorSteeringNote`).
   - Prefer `project_context` for one-call orientation. Use `dependency_inspect` only when dependencies matter.
2. **Navigate code accurately:**
   - For C#, prefer semantic Roslyn tools: `symbol_search`, `go_to_definition`, `find_references`, `find_implementations`, `inspect_type`, `compiler_diagnostics`.
   - For literal/pattern lookups or other languages: `filename_search`, `text_search`, `regex_search`. Prefer `read_context` for related files/ranges; for a single range use `read_range` with `format: "formatted"` to avoid duplicate source.
3. **Edit safely:**
   - Read target files first and pass `expectedSha256` into `apply_patch`, `replace_range`, or `create_file` (`overwrite: true`) to prevent clobbering concurrent changes.
   - Use `preview_edit` for dry-runs and `revert_edit` (with returned `editId`) if a change must be rolled back.
4. **Build, test, and inspect logs:**
   - Run only relevant verification for the change: `dotnet_build`, `msbuild_run`, `dotnet_test` (returns exact `total`, `passed`, `failed`, `skipped`, and `failures`), or `run_command`.
   - Command responses include concise excerpts and `executionId`; paginate full output with `read_log` (`execId`, `startLine`, `maxLines`, `contains`).
5. **Use async jobs and DAG workflows for long operations:**
   - Call `job_start` (`{ "tool": "dotnet_test", "arguments": {} }`), poll `job_status` / `job_events` (`afterEventId`), cancel via `job_cancel`.
   - Chain multi-step tasks with `workflow_run` (supports `dependsOn`, `runIf`, `retries`, `timeoutSeconds`, and `checkpointAfter`).
6. **Commit locally:**
   - Review `git_diff` and `git_status`, stay on `main`, and commit with `git_commit`. Do not create branches, worktrees, or pull requests unless the owner explicitly authorizes an exception and changes the local policy.

## Boundaries & Limitations

- Only clients supporting MCP, authenticated HTTP, or shell execution with network reachability can invoke tools.
- Paths escaping the trusted workspace (including junctions/symlinks) and edits to `.git/hooks` or `.localidebridge.json` are rejected.
- `git_push` is blocked by default unless explicitly authorized by the local user.
- The server stays online only while `LocalIdeBridge` is open; local async jobs pause/stop if the user exits the application, and crashed non-idempotent jobs are marked `interrupted_restart` rather than replayed blindly.


## Reconnecting safely after an uncertain tool result

For non-idempotent actions prefer the `execute_once` tool with a stable `operationId`.
When connection drops, call `operation_status` or retry `execute_once` using the SAME operationId and arguments; RAMCA will not repeat a previously reserved side effect.
If status is `interrupted_unknown`, inspect workspace and logs manually before creating a new action.
See [Execution Continuity](EXECUTION_CONTINUITY.md).

## Compact transport

Set `integrations.mcpResultMode` in `config.json` to `text` for clients that consume text or `structured` for clients verified to consume structuredContent. `auto` retains existing protocol behavior. No capability is removed. Use a single result representation when supported; do not assume the model sees both wire copies.

For repeated reads, supply `ifSha256` for each file only when you already have the requested content at that version. An unchanged file returns metadata without source. Duplicate paths and overlapping ranges are combined. Use the returned version to guard edits.

Single-agent MCP discovery is the default: the ten `board_*` schemas are omitted without removing their registry implementations. To use the multi-chat board, set `integrations.advertiseAgentBoardTools` to `true`, restart RAMCA, and refresh the client's tools. HTTP/CLI discovery remains complete. This is a context optimization, not a security boundary.

## Compact MCP discovery

Set `integrations.mcpToolProfile` to `compact` and restart RAMCA to advertise only 18 routine tools (plus the board if enabled). Use `tool_discover` with an exact name to obtain its schema; `tool_read` invokes discovered read-only operations; `execute_once` or `job_start` runs specialist mutations. No tools are removed from the registry. The existing `standard` profile preserves direct discovery and remains the default for existing configurations. Refresh the client catalog after changing profiles.

For transport-level replay protection, MCP callers can put `ramca/workspace` (trusted alias or absolute path) and `ramca/operationId` (stable ID for the logical action) into `params._meta` of `tools/call`. HTTP generic invocation accepts `X-RAMCA-Workspace` and `X-RAMCA-Operation-Id` headers. The workspace is pinned without changing the supervisor selection; operation IDs route through the durable ledger. Never derive the operation ID from a reusable JSON-RPC request ID. Clients unable to send metadata should use `execute_once` directly. This does not yet isolate implicit workspace selection between all clients.

For truncated `read_context` ranges, pass the returned `nextRange` as the next range and `expectedSha256` as the original version. This resumes at an exact 1-based line and UTF-16 column, even in a long minified line, and refuses to splice content from a changed file. Omitted ranges now cover the whole file under the byte budget, rather than silently stopping at line 2000.

Use `text_search` with `queries: ["first", "second", ...]` to search several literal needles in one pass (up to 16). Match snippets are centered on the hit, including long JavaScript lines; regex search stops on its first timeout and marks the result partial.

For frequent `git_status`, pass `recentCommits:0`; use `maxFiles` and `offset` for changed-file pages, and `ifVersion` to suppress unchanged status entries. The status version describes Git's status output, not file contents or the current commit SHA. `git_diff` defaults to a 16,000-character page; use `nextOffset` and `expectedVersion` to continue safely, or scope to a file. Do not treat a truncated diff as complete.

Use `tool_performance` (via `tool_read` in compact mode) to inspect real per-tool handler and SQLite audit costs. The host keeps only fixed-size counters and latency buckets, not a growing timing history. Audit insertion and session counters commit together in one durable transaction; synchronous durability remains FULL.
