# RAMCA — Agent quickstart

Use the live MCP/HTTP/CLI connection, not a pasted tool list. The user chooses a trusted workspace. Authentication is a Bearer credential; never print the key. The listener runs only while RAMCA is open.

## Operating loop

1. **Orient once.** Use `project_context` for workspace, Git status, project manifests, and the shallow tree. Use `workspace_status` alone for a quick branch check. Stay on `main` unless the owner authorizes otherwise.
2. **Read efficiently.** Use `text_search` with `queries` (up to 16 literals in one scan), focused `symbol_search` for C# declarations, and `read_context` for related source files/ranges. For one small range use `read_range` with `format: "formatted"`; its default legacy representation includes both formats. Do not repeatedly read unchanged files.
3. **Implement and verify.** Use `apply_patch` or version-guarded edits. Run only verification relevant to the risk and scope of the change; do not mechanically run full suites on every commit. Use `workflow_run` for known independent reads and dependent operations, not for steps that require new model reasoning.
4. **Wait efficiently.** Use a synchronous call for fast work. For long builds/tests, `job_start`, then `job_events` with `afterEventId`, `waitMs:25000`, `waitForTerminal:true`, `includeResult:true`. Use `job_status` with `includeResult:false` if only status is needed. Use `read_log` for relevant excerpts, not entire logs.
5. **Review and commit.** Use `git_status` with `recentCommits:0` for frequent checks, and focused `git_diff` before coherent commits. Never create extra branches/worktrees or push externally unless authorized.

## Compact reads and workflows

- `read_context`: up to 32 files / 64 ranges, with a 24,000-byte default data budget. Each item takes `path`, optional `ranges:[[startLine,endLine,startColumn?],...]`, `ifSha256` (only if the requested source is already known), and `expectedSha256` (required version for safe continuation). Omitted ranges cover the whole file within budget. Follow `nextRange` on truncation, and stop/re-read if the version changes. Overlapping ranges are combined.
- `read_range`: a bounded legacy line read (32,000 source characters by default); narrow the range or use `read_context` when the requested lines exceed the limit.
- `workflow_run`: use `dependsOn` and result bindings (`$from` with a dependency step and JSON pointer) for known dependencies. Each step may also specify up to eight `returnPointers` into the result (e.g. `/data/content`), plus `returnMaxBytes` (512–16384, default 8192; 64 KiB aggregate). Final response returns selected `outputs`, not every intermediate result. Projection errors do not replay a completed mutation; durable checkpoints contain summaries only.
- `git_status`: `maxFiles`, `offset`, and `ifVersion` bound repeated status reads. The status version is not a content or commit version. `git_diff` returns a default 16,000-character page; continue with `nextOffset` and `expectedVersion` or scope to a file.
- `symbol_search` can filter by file pattern and declaration kind; source spans are syntax-level, not full project-aware semantic analysis.

## Connection and compatibility

- MCP: `<BASE_URL>/mcp`; HTTP: `<BASE_URL>/api/v1`; CLI: `LocalIdeBridge cli --endpoint <BASE_URL> <command> --json`. Send `Authorization: Bearer <KEY>` or `X-LocalIdeBridge-Key: <KEY>`.
- Keep `integrations.mcpToolProfile: "standard"` for direct access to routine tools. The optional `compact` profile advertises 18 tools and uses `tool_discover` / `tool_read` / `execute_once` for specialists. A smaller catalog can cost extra discovery calls; choose based on measured client behavior.
- `integrations.advertiseAgentBoardTools: false` (default) hides board schemas; functionality remains registered. Restart and refresh the client's tool catalog after profile changes.
- `integrations.mcpResultMode: "auto"` preserves existing clients. Try `text` only with a client verified to consume the complete text result; use `structured` only with a verified structured-content client. Confirm a small read works before trusting a single-copy mode.
- Legacy MCP sessions and stdio pin their selected workspace. Stateless callers can pass `ramca/workspace` and `ramca/operationId` in MCP `params._meta`, or HTTP headers `X-RAMCA-Workspace` and `X-RAMCA-Operation-Id`. Local durable session-history selection remains shared.

## Recovery and boundaries

For disconnect-safe side effects use `execute_once` with a stable operation ID. After uncertainty, query `operation_status` or retry with the SAME ID and arguments. An unknown outcome requires reconciliation, not blind replay. Never use a recycled JSON-RPC ID as a logical operation ID. See [Execution continuity](EXECUTION_CONTINUITY.md).

Use `tool_performance` for per-tool handler and durable-audit latency. See [Performance](PERFORMANCE.md) for the quick equivalent-work benchmark. Network/model latency and actual token usage must be measured separately.

Workspace-confined file operations do not constitute an OS sandbox for arbitrary approved executables. The local owner controls trust and external Git push. Jobs cannot survive host shutdown; interrupted mutations must be reconciled.
