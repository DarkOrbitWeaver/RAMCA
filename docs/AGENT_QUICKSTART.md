# LocalIdeBridge — Agent Quickstart

Copy this document (or press **`I`** in LocalIdeBridge / run `LocalIdeBridge.exe instructions --copy` for a version pre-filled with your live endpoint) into any compatible MCP, HTTP, or CLI coding agent. **Never paste your secret key into public files; configure it as a Bearer credential.**

## What You Are Connected To

`LocalIdeBridge` exposes 52 real software-engineering tools confined to a user-trusted workspace folder on a Windows PC.

- **MCP Streamable HTTP:** `<BASE_URL>/mcp` (`POST` JSON-RPC 2.0, `GET` SSE, `DELETE` session)
- **HTTP/HTTPS JSON API:** `<BASE_URL>/api/v1` (OpenAPI 3.1 schema at `<BASE_URL>/openapi.json`)
- **CLI Client:** `LocalIdeBridge cli --endpoint <BASE_URL> <command> --json`
- **Authentication:** `Authorization: Bearer <LOCALIDEBRIDGE_KEY>` (or `X-LocalIdeBridge-Key: <LOCALIDEBRIDGE_KEY>`)

## Recommended Operating Flow

1. **Discover & orient:**
   - Call `workspace_status` (check active workspace, Git branch, dirty count, and `supervisorSteeringNote`).
   - Call `project_discover`, `repo_tree`, and `dependency_inspect`.
2. **Navigate code accurately:**
   - For C#, prefer semantic Roslyn tools: `symbol_search`, `go_to_definition`, `find_references`, `find_implementations`, `inspect_type`, `compiler_diagnostics`.
   - For literal/pattern lookups or other languages: `filename_search`, `text_search`, `regex_search`, `read_file`, `read_range`.
3. **Edit safely:**
   - Read target files first and pass `expectedSha256` into `apply_patch`, `replace_range`, or `create_file` (`overwrite: true`) to prevent clobbering concurrent changes.
   - Use `preview_edit` for dry-runs and `revert_edit` (with returned `editId`) if a change must be rolled back.
4. **Build, test, and inspect logs:**
   - Run `dotnet_build`, `msbuild_run`, `dotnet_test` (returns exact `total`, `passed`, `failed`, `skipped`, and `failures`), or `run_command`.
   - Command responses include concise excerpts and `executionId`; paginate full output with `read_log` (`execId`, `startLine`, `maxLines`, `contains`).
5. **Use async jobs and DAG workflows for long operations:**
   - Call `job_start` (`{ "tool": "dotnet_test", "arguments": {} }`), poll `job_status` / `job_events` (`afterEventId`), cancel via `job_cancel`.
   - Chain multi-step tasks with `workflow_run` (supports `dependsOn`, `runIf`, `retries`, `timeoutSeconds`, and `checkpointAfter`).
6. **Commit locally:**
   - Review `git_diff` and `git_status`, create branches with `git_branch` (or isolated `git_worktree`), and commit with `git_commit`.

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
