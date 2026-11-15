# LocalIdeBridge — Complete Tool Reference

> Auto-generated from the authoritative `ToolRegistry` contracts (`52` tools). The exact same `ToolDefinition` objects power MCP `tools/list`, `/api/v1/tools`, `/openapi.json`, and `LocalIdeBridge cli tools`.

## Standard Response Envelope

Every tool invocation across MCP (`structuredContent`), HTTPS API (`POST /api/v1/tools/{name}/invoke`), and CLI (`LocalIdeBridge cli call <name> --json`) returns a consistent `ToolResult` contract:

```json
{
  "success": true,
  "summary": "Concise human- and AI-readable result summary.",
  "data": { },
  "errorCode": null,
  "errorMessage": null,
  "executionId": "exec_20260101_120000_a1b2c3d4"
}
```

## Standard Error Codes

| Error Code | HTTP Status | Meaning |
| --- | --- | --- |
| `unauthorized` | `401` | Missing, invalid, or rotated Bearer / `X-LocalIdeBridge-Key` credential. |
| `workspace_violation` | `403` | Path escapes trusted workspace (traversal, junction, external symlink), targets protected `.git/hooks`, or command blocked by policy. |
| `authorization_required` | `403` | Action requires explicit local configuration (e.g. `git_push` while `gitPushPolicy='deny'`). |
| `host_paused` | `403` | Local user paused write/execution tools in the supervisor TUI. |
| `invalid_arguments` / `invalid_patch` / `invalid_regex` / `range_out_of_bounds` | `400` | Schema, range, diff, or regex validation failed. |
| `not_found` / `unknown_tool` | `404` | Target file, directory, job, session, edit, log, or tool does not exist. |
| `stale_file` / `precondition_failed` / `conflict` / `already_exists` | `409` | Concurrent file edit detected (`expectedSha256` mismatch), unified diff context mismatch, or unsafe revert. |
| `timeout` | `408` | Command or workflow step exceeded timeout; owned child tree was terminated. |
| `cancelled` | `422` | Cancelled by client, `job_cancel`, `workflow_cancel`, or host shutdown. |
| `build_failed` / `tests_failed` / `command_failed` / `workflow_failed` | `422` | Underlying process or workflow finished with non-zero status; structured diagnostics and `executionId` are included in `data`. |
| `payload_too_large` | `413` | Request body exceeded `security.maxRequestBodyBytes`. |
| `rate_limited` / `auth_rate_limited` | `429` | Per-minute request or failed-auth threshold exceeded. |

## Category: `workspace` (4 tools)

### `workspace_list`

Lists all trusted workspaces configured in LocalIdeBridge and shows which workspace is currently active.

- **Read-only:** `true` | **Idempotent:** `true`
- **Side effects:** None.

**Parameters:** None (`{}`).

**Example (`POST /api/v1/tools/workspace_list/invoke` or MCP `tools/call` arguments):**
```json
{}
```

### `workspace_select`

Switches the active workspace to one of the trusted workspaces configured by the local user.

- **Read-only:** `false` | **Idempotent:** `true`
- **Side effects:** Changes the active workspace for subsequent tool calls in this host.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `workspace` | `string` | Yes | — | Path or configured alias of a trusted workspace. |

**Example (`POST /api/v1/tools/workspace_select/invoke` or MCP `tools/call` arguments):**
```json
{
  "workspace": "\u003Cworkspace\u003E"
}
```

### `workspace_status`

Returns active workspace details, current Git branch and dirty summary, active session info, supervisor steering note, and autonomy policy summary.

- **Read-only:** `true` | **Idempotent:** `true`
- **Side effects:** None.

**Parameters:** None (`{}`).

**Example (`POST /api/v1/tools/workspace_status/invoke` or MCP `tools/call` arguments):**
```json
{}
```

### `tool_doctor`

Verifies installed developer toolchains (dotnet, git, PowerShell, MSBuild, optional tunnels) and reports NAT/CGNAT/TLS network diagnostics.

- **Read-only:** `true` | **Idempotent:** `true`
- **Side effects:** Runs quick --version checks on installed tools.

**Parameters:** None (`{}`).

**Example (`POST /api/v1/tools/tool_doctor/invoke` or MCP `tools/call` arguments):**
```json
{}
```

## Category: `code_intel` (12 tools)

### `repo_tree`

Inspects the directory and file tree within the active workspace with bounded entry count and optional depth limit.

- **Read-only:** `true` | **Idempotent:** `true`
- **Side effects:** None.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `path` | `string` | No | `"."` | Relative subpath inside the workspace (defaults to workspace root). |
| `maxDepth` | `integer` | No | `4` | Maximum recursion depth (1-12). |
| `maxEntries` | `integer` | No | `250` | Maximum entries returned (1-1000). |
| `includeIgnored` | `boolean` | No | `false` | Include build/cache folders such as bin, obj, node_modules, .git. |

**Example (`POST /api/v1/tools/repo_tree/invoke` or MCP `tools/call` arguments):**
```json
{}
```

### `filename_search`

Searches filenames and relative paths in the workspace by wildcard glob (e.g. '*.cs', '**/*Test*.cs') or case-insensitive substring.

- **Read-only:** `true` | **Idempotent:** `true`
- **Side effects:** None.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `pattern` | `string` | Yes | — | Filename substring or wildcard pattern (* and ? supported). |
| `maxResults` | `integer` | No | `100` | Maximum matches to return (1-500). |
| `includeIgnored` | `boolean` | No | `false` | Include ignored directories like bin/obj/node_modules. |

**Example (`POST /api/v1/tools/filename_search/invoke` or MCP `tools/call` arguments):**
```json
{
  "pattern": "\u003Cpattern\u003E"
}
```

### `text_search`

Performs fast, streaming literal text search across workspace files. Explicitly reports engine='text-literal'.

- **Read-only:** `true` | **Idempotent:** `true`
- **Side effects:** None.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `query` | `string` | Yes | — | Literal text substring to search for. |
| `filePattern` | `string` | No | — | Optional wildcard filename filter such as '*.cs' or '*.json'. |
| `caseSensitive` | `boolean` | No | `false` | Match case strictly. |
| `maxResults` | `integer` | No | `100` | Maximum matches returned (1-300). |

**Example (`POST /api/v1/tools/text_search/invoke` or MCP `tools/call` arguments):**
```json
{
  "query": "\u003Cquery\u003E"
}
```

### `regex_search`

Performs timeout-protected streaming regular-expression search across workspace files. Reports engine='text-regex'.

- **Read-only:** `true` | **Idempotent:** `true`
- **Side effects:** None.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `pattern` | `string` | Yes | — | .NET regular expression pattern. |
| `filePattern` | `string` | No | — | Optional wildcard filename filter such as '*.cs'. |
| `caseSensitive` | `boolean` | No | `false` | Case-sensitive regex evaluation. |
| `maxResults` | `integer` | No | `100` | Maximum matches returned (1-300). |

**Example (`POST /api/v1/tools/regex_search/invoke` or MCP `tools/call` arguments):**
```json
{
  "pattern": "\u003Cpattern\u003E"
}
```

### `project_discover`

Discovers .NET solutions (.sln/.slnx), MSBuild projects (.csproj/.fsproj/.vbproj), Directory.Build.props, and package manifests in the workspace.

- **Read-only:** `true` | **Idempotent:** `true`
- **Side effects:** None.

**Parameters:** None (`{}`).

**Example (`POST /api/v1/tools/project_discover/invoke` or MCP `tools/call` arguments):**
```json
{}
```

### `dependency_inspect`

Inspects NuGet PackageReferences, ProjectReferences, target frameworks, and package.json dependencies for a specific project or the entire workspace.

- **Read-only:** `true` | **Idempotent:** `true`
- **Side effects:** None.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `projectPath` | `string` | No | — | Optional relative path to a .csproj/.fsproj or package.json; inspects all discovered projects when omitted. |

**Example (`POST /api/v1/tools/dependency_inspect/invoke` or MCP `tools/call` arguments):**
```json
{}
```

### `symbol_search`

Searches declared symbols (classes, interfaces, records, structs, enums, methods, properties) using Roslyn for C# files and labeled text fallback for other languages.

- **Read-only:** `true` | **Idempotent:** `true`
- **Side effects:** None.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `query` | `string` | Yes | — | Symbol name or substring to match (empty string lists top symbols). |
| `kind` | `string` | No | `"any"` | Optional filter: any | type | method | property | enum | interface. Allowed: `any`, `type`, `method`, `property`, `enum`, `interface`. |
| `maxResults` | `integer` | No | `80` | Maximum symbols returned (1-200). |

**Example (`POST /api/v1/tools/symbol_search/invoke` or MCP `tools/call` arguments):**
```json
{
  "query": "\u003Cquery\u003E"
}
```

### `go_to_definition`

Resolves the definition location(s) of a C# symbol using the Roslyn semantic model (either by file+line+column position or by symbolName), with explicit text fallback for non-C#.

- **Read-only:** `true` | **Idempotent:** `true`
- **Side effects:** None.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `symbolName` | `string` | No | — | Symbol name to locate when file/line/column are omitted. |
| `file` | `string` | No | — | Optional relative C# file path containing a symbol usage. |
| `line` | `integer` | No | — | 1-based line number of symbol usage (when file is specified). |
| `column` | `integer` | No | — | 1-based column number of symbol usage (when file is specified). |

**Example (`POST /api/v1/tools/go_to_definition/invoke` or MCP `tools/call` arguments):**
```json
{}
```

### `find_references`

Finds semantic references to a C# symbol across the workspace using Roslyn SemanticModel, clearly distinguished from text fallback.

- **Read-only:** `true` | **Idempotent:** `true`
- **Side effects:** None.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `symbolName` | `string` | No | — | Symbol name to find references for (when file/line/column are omitted). |
| `file` | `string` | No | — | Optional relative C# file path at symbol position. |
| `line` | `integer` | No | — | 1-based line number (with file). |
| `column` | `integer` | No | — | 1-based column number (with file). |
| `maxResults` | `integer` | No | `100` | Maximum references returned (1-200). |

**Example (`POST /api/v1/tools/find_references/invoke` or MCP `tools/call` arguments):**
```json
{}
```

### `find_implementations`

Finds concrete types implementing an interface, derived subclasses of a base type, or overrides/implementations of a method/property using Roslyn semantics.

- **Read-only:** `true` | **Idempotent:** `true`
- **Side effects:** None.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `symbolName` | `string` | Yes | — | Interface, base class, or abstract/virtual member name. |
| `maxResults` | `integer` | No | `50` | Maximum implementations returned (1-100). |

**Example (`POST /api/v1/tools/find_implementations/invoke` or MCP `tools/call` arguments):**
```json
{
  "symbolName": "\u003CsymbolName\u003E"
}
```

### `inspect_type`

Inspects a C# type or method using Roslyn, returning namespace, base type, interfaces, constructors, properties, method signatures, and source location.

- **Read-only:** `true` | **Idempotent:** `true`
- **Side effects:** None.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `typeName` | `string` | Yes | — | Simple or fully-qualified C# type name (e.g. 'Calculator' or 'MyApp.Calculator'). |
| `memberName` | `string` | No | — | Optional method or property name on the type for focused signature inspection. |

**Example (`POST /api/v1/tools/inspect_type/invoke` or MCP `tools/call` arguments):**
```json
{
  "typeName": "\u003CtypeName\u003E"
}
```

### `compiler_diagnostics`

Collects structured C# compiler errors and warnings either via full MSBuild/dotnet build ('build' mode, includes NuGet packages) or fast in-process Roslyn syntax/semantic check ('roslyn' mode).

- **Read-only:** `true` | **Idempotent:** `true`
- **Side effects:** In 'build' mode runs dotnet build; in 'roslyn' mode performs read-only in-memory analysis.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `mode` | `string` | No | `"build"` | build (accurate with NuGet references) | roslyn (fast in-memory). Allowed: `build`, `roslyn`. |
| `target` | `string` | No | — | Optional relative .sln or .csproj path (for 'build' mode). |
| `includeWarnings` | `boolean` | No | `true` | Include warning diagnostics in addition to errors. |

**Example (`POST /api/v1/tools/compiler_diagnostics/invoke` or MCP `tools/call` arguments):**
```json
{}
```

## Category: `editing` (11 tools)

### `create_directory`

Creates a directory (and any missing parent directories) inside the trusted workspace.

- **Read-only:** `false` | **Idempotent:** `true`
- **Side effects:** Creates directory on disk inside the active workspace.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `path` | `string` | Yes | — | Relative directory path inside the active workspace. |

**Example (`POST /api/v1/tools/create_directory/invoke` or MCP `tools/call` arguments):**
```json
{
  "path": "\u003Cpath\u003E"
}
```

### `read_file`

Reads a text file inside the workspace, returning content, line count, encoding, line ending style, and SHA-256 version token for safe edits.

- **Read-only:** `true` | **Idempotent:** `true`
- **Side effects:** None.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `path` | `string` | Yes | — | Relative file path inside the workspace. |
| `maxChars` | `integer` | No | `32000` | Maximum characters to return before truncating (use read_range for huge files). |
| `includeLineNumbers` | `boolean` | No | `false` | Prefix each returned line with its 1-based line number. |

**Example (`POST /api/v1/tools/read_file/invoke` or MCP `tools/call` arguments):**
```json
{
  "path": "\u003Cpath\u003E"
}
```

### `read_range`

Streams a bounded 1-based inclusive line range [startLine, endLine] from a workspace file without loading huge files entirely into memory.

- **Read-only:** `true` | **Idempotent:** `true`
- **Side effects:** None.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `path` | `string` | Yes | — | Relative file path inside the workspace. |
| `startLine` | `integer` | Yes | — | 1-based starting line number. |
| `endLine` | `integer` | Yes | — | 1-based inclusive ending line number. |

**Example (`POST /api/v1/tools/read_range/invoke` or MCP `tools/call` arguments):**
```json
{
  "path": "\u003Cpath\u003E",
  "startLine": 1,
  "endLine": 1
}
```

### `create_file`

Creates a new file (or atomically overwrites an existing file when overwrite=true) inside the trusted workspace and records a reversible edit entry.

- **Read-only:** `false` | **Idempotent:** `false`
- **Side effects:** Writes a file inside the trusted workspace and stores a journal entry in data/sessions.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `path` | `string` | Yes | — | Relative file path inside the workspace. |
| `content` | `string` | Yes | — | Full text content to write. |
| `overwrite` | `boolean` | No | `false` | Allow replacing an existing file. |
| `lineEnding` | `string` | No | `"preserve"` | preserve | CRLF | LF (defaults to preserve existing or platform default). Allowed: `preserve`, `CRLF`, `LF`. |
| `expectedSha256` | `string` | No | — | Optional SHA-256 of existing file when overwriting to detect concurrent edits. |

**Example (`POST /api/v1/tools/create_file/invoke` or MCP `tools/call` arguments):**
```json
{
  "path": "\u003Cpath\u003E",
  "content": "\u003Ccontent\u003E"
}
```

### `replace_range`

Atomically replaces 1-based inclusive lines [startLine, endLine] in a file while preserving encoding and line endings, with optional SHA-256 and old-text verification.

- **Read-only:** `false` | **Idempotent:** `false`
- **Side effects:** Atomically replaces target file inside workspace and records a reversible edit.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `path` | `string` | Yes | — | Relative file path inside the workspace. |
| `startLine` | `integer` | Yes | — | 1-based inclusive start line to replace. |
| `endLine` | `integer` | Yes | — | 1-based inclusive end line to replace. |
| `newText` | `string` | Yes | — | Replacement text for the specified line range. |
| `expectedSha256` | `string` | No | — | Optional SHA-256 returned by read_file/read_range to prevent overwriting concurrent changes. |
| `expectedOldText` | `string` | No | — | Optional exact text expected in the target range as an additional precondition check. |

**Example (`POST /api/v1/tools/replace_range/invoke` or MCP `tools/call` arguments):**
```json
{
  "path": "\u003Cpath\u003E",
  "startLine": 1,
  "endLine": 1,
  "newText": "\u003CnewText\u003E"
}
```

### `apply_patch`

Applies a standard unified diff (single-file or multi-file) atomically after verifying all hunk context preconditions. Preserves encoding and line endings.

- **Read-only:** `false` | **Idempotent:** `false`
- **Side effects:** Atomically modifies/creates/deletes patched files in workspace and records reversible edit entries.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `patch` | `string` | Yes | — | Unified diff text (with --- / +++ file headers and @@ hunk headers). |
| `expectedSha256` | `string` | No | — | Optional SHA-256 precondition (for single-file patches). |

**Example (`POST /api/v1/tools/apply_patch/invoke` or MCP `tools/call` arguments):**
```json
{
  "patch": "\u003Cpatch\u003E"
}
```

### `preview_edit`

Validates and previews a unified diff or bounded line replacement without modifying any file on disk.

- **Read-only:** `true` | **Idempotent:** `true`
- **Side effects:** None (dry-run only).

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `patch` | `string` | No | — | Optional unified diff to validate and preview. |
| `path` | `string` | No | — | File path (when previewing a range replacement instead of a patch). |
| `startLine` | `integer` | No | — | 1-based start line (for range preview). |
| `endLine` | `integer` | No | — | 1-based end line (for range preview). |
| `newText` | `string` | No | — | Proposed replacement text (for range preview). |

**Example (`POST /api/v1/tools/preview_edit/invoke` or MCP `tools/call` arguments):**
```json
{}
```

### `rename_file`

Renames or moves a file inside the trusted workspace.

- **Read-only:** `false` | **Idempotent:** `false`
- **Side effects:** Moves file within trusted workspace.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `sourcePath` | `string` | Yes | — | Existing relative file path. |
| `destinationPath` | `string` | Yes | — | New relative file path inside workspace. |
| `overwrite` | `boolean` | No | `false` | Overwrite destination if it exists. |

**Example (`POST /api/v1/tools/rename_file/invoke` or MCP `tools/call` arguments):**
```json
{
  "sourcePath": "\u003CsourcePath\u003E",
  "destinationPath": "\u003CdestinationPath\u003E"
}
```

### `delete_file`

Deletes a workspace file after preserving a verified undo backup up to 256 MiB. Larger files require explicit `allowIrreversible=true`, and those deletions cannot be undone.

- **Read-only:** `false` | **Idempotent:** `false`
- **Side effects:** Deletes the target file. A backup is retained unless the caller explicitly authorized an oversized irreversible deletion.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `path` | `string` | Yes | — | Relative file path to delete. |
| `expectedSha256` | `string` | No | — | Optional SHA-256 precondition check. |
| `allowIrreversible` | `boolean` | No | `false` | Explicitly allow deleting a file larger than 256 MiB without an undo backup. |

**Example (`POST /api/v1/tools/delete_file/invoke` or MCP `tools/call` arguments):**
```json
{
  "path": "\u003Cpath\u003E"
}
```

### `inspect_diff`

Lazy-loads a unified diff for a recorded editId (before vs current file) or lists recent session edits.

- **Read-only:** `true` | **Idempotent:** `true`
- **Side effects:** None.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `editId` | `string` | No | — | Optional specific editId to compute unified diff for. |
| `limit` | `integer` | No | `15` | Number of recent edit summaries to return when editId is omitted. |

**Example (`POST /api/v1/tools/inspect_diff/invoke` or MCP `tools/call` arguments):**
```json
{}
```

### `revert_edit`

Safely reverts a specific recorded editId ONLY if the file has not been modified since that edit (verified by SHA-256).

- **Read-only:** `false` | **Idempotent:** `false`
- **Side effects:** Restores pre-edit file bytes (or deletes newly created file) inside the workspace.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `editId` | `string` | Yes | — | Edit identifier returned by create_file, replace_range, apply_patch, or delete_file. |

**Example (`POST /api/v1/tools/revert_edit/invoke` or MCP `tools/call` arguments):**
```json
{
  "editId": "\u003CeditId\u003E"
}
```

## Category: `execution` (3 tools)

### `run_command`

Executes an approved developer executable or PowerShell command inside the trusted workspace with timeout, cancellation, structured metadata, and on-demand log reference.

- **Read-only:** `false` | **Idempotent:** `false`
- **Side effects:** Executes a supervised child process inside the workspace (terminated automatically on timeout, cancellation, or host exit).

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `command` | `string` | Yes | — | PowerShell script/command text (when executable is omitted) or executable name/workspace-relative binary path. |
| `arguments` | `array` | No | — | Optional argument array when invoking an executable directly instead of a shell script. |
| `useShell` | `boolean` | No | `false` | Execute 'command' via PowerShell (pwsh/powershell on Windows, pwsh/bash fallback elsewhere). |
| `allowWorkspaceExecutable` | `boolean` | No | `false` | Permit executing a compiled binary located strictly inside the active workspace. |
| `workingDirectory` | `string` | No | — | Optional relative subdirectory inside the active workspace. |
| `timeoutSeconds` | `integer` | No | — | Execution timeout in seconds (clamped to config resources.maxCommandTimeoutSeconds). |

**Example (`POST /api/v1/tools/run_command/invoke` or MCP `tools/call` arguments):**
```json
{
  "command": "\u003Ccommand\u003E"
}
```

### `read_log`

Retrieves a paginated line range from a stored command, build, or test execution log on disk without loading full logs into memory.

- **Read-only:** `true` | **Idempotent:** `true`
- **Side effects:** None.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `execId` | `string` | Yes | — | Execution identifier returned by run_command, dotnet_build, msbuild_run, or dotnet_test. |
| `startLine` | `integer` | No | `1` | 1-based line number to begin reading from. |
| `maxLines` | `integer` | No | `120` | Maximum lines to return (1-500). |
| `contains` | `string` | No | — | Optional case-insensitive substring filter (e.g. 'error'). |

**Example (`POST /api/v1/tools/read_log/invoke` or MCP `tools/call` arguments):**
```json
{
  "execId": "\u003CexecId\u003E"
}
```

### `process_diagnostics`

Inspects LocalIdeBridge host resource usage (working set, GC managed heap, threads, uptime, Windows Job Object state) and active owned child processes.

- **Read-only:** `true` | **Idempotent:** `true`
- **Side effects:** None.

**Parameters:** None (`{}`).

**Example (`POST /api/v1/tools/process_diagnostics/invoke` or MCP `tools/call` arguments):**
```json
{}
```

## Category: `build` (2 tools)

### `dotnet_build`

Builds a .NET project or solution inside the trusted workspace and returns structured compiler diagnostics plus log retrieval reference.

- **Read-only:** `false` | **Idempotent:** `true`
- **Side effects:** Compiles build outputs inside the workspace.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `target` | `string` | No | — | Optional relative .sln/.csproj path (defaults to workspace directory). |
| `configuration` | `string` | No | `"Debug"` | Build configuration (Debug or Release). |
| `noRestore` | `boolean` | No | `false` | Skip automatic NuGet restore. |
| `extraArgs` | `array` | No | — | Optional additional dotnet build flags. |
| `timeoutSeconds` | `integer` | No | — | Optional build timeout in seconds. |

**Example (`POST /api/v1/tools/dotnet_build/invoke` or MCP `tools/call` arguments):**
```json
{}
```

### `msbuild_run`

Invokes MSBuild (standalone Visual Studio MSBuild when installed, or 'dotnet msbuild' fallback) with structured compiler diagnostics.

- **Read-only:** `false` | **Idempotent:** `true`
- **Side effects:** Executes MSBuild targets inside the workspace.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `project` | `string` | No | — | Optional relative project or solution file. |
| `target` | `string` | No | `"Build"` | MSBuild target name (e.g. Build, Rebuild, Clean). |
| `configuration` | `string` | No | `"Debug"` | Configuration property value. |
| `timeoutSeconds` | `integer` | No | — | Optional timeout in seconds. |

**Example (`POST /api/v1/tools/msbuild_run/invoke` or MCP `tools/call` arguments):**
```json
{}
```

## Category: `test` (1 tools)

### `dotnet_test`

Runs 'dotnet test' with TRX structured reporting, returning exact total, passed, failed, skipped counts, failure stack traces, and log retrieval ID.

- **Read-only:** `false` | **Idempotent:** `true`
- **Side effects:** Builds (unless noBuild=true) and runs tests in workspace; writes temporary TRX inside LocalIdeBridge/temp.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `target` | `string` | No | — | Optional relative test project or solution path. |
| `filter` | `string` | No | — | Optional VSTest --filter expression for focused test runs. |
| `configuration` | `string` | No | `"Debug"` | Configuration (Debug or Release). |
| `noBuild` | `boolean` | No | `false` | Skip build step before running tests. |
| `timeoutSeconds` | `integer` | No | — | Optional test run timeout in seconds. |

**Example (`POST /api/v1/tools/dotnet_test/invoke` or MCP `tools/call` arguments):**
```json
{}
```

## Category: `git` (8 tools)

### `git_status`

Returns structured Git branch, modified/staged/untracked file entries, and recent local commits for the active workspace.

- **Read-only:** `true` | **Idempotent:** `true`
- **Side effects:** None.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `recentCommits` | `integer` | No | `5` | Number of recent commits to include (0-25). |

**Example (`POST /api/v1/tools/git_status/invoke` or MCP `tools/call` arguments):**
```json
{}
```

### `git_diff`

Compares working-tree or staged changes against Git HEAD (or a specified revision/path).

- **Read-only:** `true` | **Idempotent:** `true`
- **Side effects:** None.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `staged` | `boolean` | No | `false` | Inspect staged (--cached) diff instead of unstaged changes. |
| `revision` | `string` | No | — | Optional commit/branch ref to compare against (e.g. 'HEAD~1'). |
| `path` | `string` | No | — | Optional relative file path inside workspace. |

**Example (`POST /api/v1/tools/git_diff/invoke` or MCP `tools/call` arguments):**
```json
{}
```

### `git_branch`

Lists, creates, or switches local Git branches (or initializes a new Git repository in the workspace when action='init').

- **Read-only:** `false` | **Idempotent:** `false`
- **Side effects:** Initializes repo or creates/switches local Git branch inside workspace.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `action` | `string` | No | `"list"` | list | create | switch | create_and_switch | init Allowed: `list`, `create`, `switch`, `create_and_switch`, `init`. |
| `name` | `string` | No | — | Branch name (required for create/switch/create_and_switch; optional initial branch for init). |
| `startPoint` | `string` | No | — | Optional starting commit or branch when creating. |

**Example (`POST /api/v1/tools/git_branch/invoke` or MCP `tools/call` arguments):**
```json
{}
```

### `git_worktree`

Lists, creates, or removes local Git worktrees confined to the active workspace or LocalIdeBridge/sandbox.

- **Read-only:** `false` | **Idempotent:** `false`
- **Side effects:** Creates or removes a Git worktree directory.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `action` | `string` | No | `"list"` | list | add | remove Allowed: `list`, `add`, `remove`. |
| `name` | `string` | No | — | Worktree folder name (created under LocalIdeBridge/sandbox/worktrees/<name>) or workspace-relative path. |
| `branch` | `string` | No | — | Branch name to create (-b) or checkout when adding a worktree. |

**Example (`POST /api/v1/tools/git_worktree/invoke` or MCP `tools/call` arguments):**
```json
{}
```

### `git_stage`

Stages specified workspace files (or all modified/new files when all=true) into the local Git index.

- **Read-only:** `false` | **Idempotent:** `true`
- **Side effects:** Updates local .git/index.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `paths` | `array` | No | — | Relative file paths to stage. |
| `all` | `boolean` | No | `false` | Stage all workspace changes (git add -A). |

**Example (`POST /api/v1/tools/git_stage/invoke` or MCP `tools/call` arguments):**
```json
{}
```

### `git_commit`

Creates a local Git commit in the active workspace without pushing externally, and records the commit SHA in session checkpoints.

- **Read-only:** `false` | **Idempotent:** `false`
- **Side effects:** Creates a local Git commit and persists a session checkpoint.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `message` | `string` | Yes | — | Commit message. |
| `stageAll` | `boolean` | No | `false` | Automatically stage all changes (git add -A) before committing. |
| `authorName` | `string` | No | — | Optional fallback author name if repo has no git user.name configured. |
| `authorEmail` | `string` | No | — | Optional fallback author email if repo has no git user.email configured. |

**Example (`POST /api/v1/tools/git_commit/invoke` or MCP `tools/call` arguments):**
```json
{
  "message": "\u003Cmessage\u003E"
}
```

### `git_checkpoint`

Records the current local Git HEAD commit and branch as a durable named session checkpoint (or lists existing checkpoints).

- **Read-only:** `false` | **Idempotent:** `true`
- **Side effects:** Persists a checkpoint record in data/state.db.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `label` | `string` | No | — | Human/AI-readable label for this checkpoint (omit to list recent checkpoints). |

**Example (`POST /api/v1/tools/git_checkpoint/invoke` or MCP `tools/call` arguments):**
```json
{}
```

### `git_push`

Pushes local commits to a configured remote ONLY when autonomy.gitPushPolicy is explicitly set to 'allow' by the local owner (blocked by default).

- **Read-only:** `false` | **Idempotent:** `false`
- **Side effects:** Publishes commits externally when authorized; rejected by default.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `remote` | `string` | No | `"origin"` | Remote name. |
| `branch` | `string` | No | — | Optional branch name. |

**Example (`POST /api/v1/tools/git_push/invoke` or MCP `tools/call` arguments):**
```json
{}
```

## Category: `jobs` (5 tools)

### `job_start`

Starts any LocalIdeBridge tool asynchronously as a background job and returns a jobId immediately for polling, event retrieval, or cancellation.

- **Read-only:** `false` | **Idempotent:** `false`
- **Side effects:** Enqueues and executes the specified tool asynchronously; persists job record in data/state.db.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `tool` | `string` | Yes | — | Name of the tool to run asynchronously (e.g. 'dotnet_build', 'dotnet_test', 'workflow_run'). |
| `arguments` | `object` | No | — | Arguments object passed to the target tool. |
| `timeoutSeconds` | `integer` | No | — | Optional job timeout in seconds. |

**Example (`POST /api/v1/tools/job_start/invoke` or MCP `tools/call` arguments):**
```json
{
  "tool": "\u003Ctool\u003E"
}
```

### `job_status`

Inspects the current status, timestamps, result payload, error details, and log reference of an asynchronous job.

- **Read-only:** `true` | **Idempotent:** `true`
- **Side effects:** None.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `jobId` | `string` | Yes | — | Job identifier returned by job_start. |

**Example (`POST /api/v1/tools/job_status/invoke` or MCP `tools/call` arguments):**
```json
{
  "jobId": "\u003CjobId\u003E"
}
```

### `job_list`

Returns a paginated list of asynchronous jobs (filterable by sessionId or status).

- **Read-only:** `true` | **Idempotent:** `true`
- **Side effects:** None.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `sessionId` | `string` | No | — | Optional session ID filter (omit to list across sessions). |
| `status` | `string` | No | — | Optional status filter: queued | running | succeeded | failed | cancelled | interrupted_restart. |
| `limit` | `integer` | No | `25` | Page size (1-100). |
| `offset` | `integer` | No | `0` | Pagination offset. |

**Example (`POST /api/v1/tools/job_list/invoke` or MCP `tools/call` arguments):**
```json
{}
```

### `job_events`

Retrieves incremental persisted events for a job after a given event ID (safe across network reconnects).

- **Read-only:** `true` | **Idempotent:** `true`
- **Side effects:** None.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `jobId` | `string` | Yes | — | Job identifier. |
| `afterEventId` | `integer` | No | `0` | Return only events strictly greater than this ID. |
| `limit` | `integer` | No | `100` | Maximum events returned (1-200). |

**Example (`POST /api/v1/tools/job_events/invoke` or MCP `tools/call` arguments):**
```json
{
  "jobId": "\u003CjobId\u003E"
}
```

### `job_cancel`

Requests cancellation of an active background job and terminates any child processes owned by that job.

- **Read-only:** `false` | **Idempotent:** `true`
- **Side effects:** Cancels job and kills its owned child process tree.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `jobId` | `string` | Yes | — | Job identifier to cancel. |

**Example (`POST /api/v1/tools/job_cancel/invoke` or MCP `tools/call` arguments):**
```json
{
  "jobId": "\u003CjobId\u003E"
}
```

## Category: `sessions` (3 tools)

### `session_list`

Returns a paginated list of disk-backed LocalIdeBridge sessions.

- **Read-only:** `true` | **Idempotent:** `true`
- **Side effects:** None.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `limit` | `integer` | No | `20` | Page size (1-100). |
| `offset` | `integer` | No | `0` | Pagination offset. |

**Example (`POST /api/v1/tools/session_list/invoke` or MCP `tools/call` arguments):**
```json
{}
```

### `session_inspect`

Inspects a session's paginated tool call history, recent edits, jobs, error history, Git checkpoints, and steering note.

- **Read-only:** `true` | **Idempotent:** `true`
- **Side effects:** None.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `sessionId` | `string` | No | — | Session ID to inspect (defaults to active session). |
| `toolCallLimit` | `integer` | No | `25` | Maximum recent tool calls to include (1-100). |
| `toolCallOffset` | `integer` | No | `0` | Pagination offset for tool calls. |

**Example (`POST /api/v1/tools/session_inspect/invoke` or MCP `tools/call` arguments):**
```json
{}
```

### `session_switch`

Switches to an existing disk-backed session by sessionId or creates a fresh active session when action='new'.

- **Read-only:** `false` | **Idempotent:** `false`
- **Side effects:** Changes the active session ID recorded in data/state.db.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `action` | `string` | No | `"switch"` | switch | new Allowed: `switch`, `new`. |
| `sessionId` | `string` | No | — | Target session ID when action='switch'. |

**Example (`POST /api/v1/tools/session_switch/invoke` or MCP `tools/call` arguments):**
```json
{}
```

## Category: `workflows` (3 tools)

### `workflow_run`

Executes a dependency-aware DAG workflow with parallel independent read-only steps, write-locked sequential modifications, conditional runIf branching, retries, timeouts, and durable checkpoints.

- **Read-only:** `false` | **Idempotent:** `false`
- **Side effects:** Executes configured workflow steps and persists workflow state in data/state.db.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `name` | `string` | No | `"workflow"` | Human-readable workflow name. |
| `steps` | `array` | Yes | — | Ordered list of workflow step objects: { id, tool, arguments?, dependsOn?, runIf?, timeoutSeconds?, retries?, continueOnError?, checkpointAfter? }. |
| `maxParallelism` | `integer` | No | — | Optional max concurrency for independent read-only steps. |
| `async` | `boolean` | No | `false` | If true, launches the workflow as a background job and returns jobId immediately. |

**Example (`POST /api/v1/tools/workflow_run/invoke` or MCP `tools/call` arguments):**
```json
{
  "steps": []
}
```

### `workflow_status`

Retrieves the persisted execution state and per-step results of a workflow run.

- **Read-only:** `true` | **Idempotent:** `true`
- **Side effects:** None.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `workflowId` | `string` | Yes | — | Workflow run identifier. |

**Example (`POST /api/v1/tools/workflow_status/invoke` or MCP `tools/call` arguments):**
```json
{
  "workflowId": "\u003CworkflowId\u003E"
}
```

### `workflow_cancel`

Cancels an actively executing workflow run.

- **Read-only:** `false` | **Idempotent:** `true`
- **Side effects:** Cancels pending and active steps in the target workflow.

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `workflowId` | `string` | Yes | — | Workflow run identifier. |

**Example (`POST /api/v1/tools/workflow_cancel/invoke` or MCP `tools/call` arguments):**
```json
{
  "workflowId": "\u003CworkflowId\u003E"
}
```

