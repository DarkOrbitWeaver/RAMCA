# Architecture

LocalIdeBridge is a single cohesive C# / .NET 10 executable (`src/LocalIdeBridge`) with zero external services, zero web UI bloat, and two focused NuGet dependencies: `Microsoft.Data.Sqlite` and `Microsoft.CodeAnalysis.CSharp` (Roslyn), hosted on ASP.NET Core Kestrel (`WebApplication.CreateSlimBuilder`).

## Component Diagram

```text
 External AI Clients
 ┌──────────────────────┐  ┌──────────────────────┐  ┌──────────────────────┐
 │ MCP Clients          │  │ Programmable Agents  │  │ Shell Agents         │
 │ Streamable HTTP /mcp │  │ HTTPS API /api/v1/*  │  │ LocalIdeBridge cli   │
 │ or local mcp-stdio   │  │ + /openapi.json      │  │ (JSON + exit codes)  │
 └──────────┬───────────┘  └──────────┬───────────┘  └──────────┬───────────┘
            └─────────────────────────┼─────────────────────────┘
                                      ▼
                     ┌────────────────────────────────┐
                     │ SecurityMiddleware             │
                     │ Auth, Rate Limit, Size, Origin │
                     └────────────────┬───────────────┘
                                      ▼
    ┌────────────────┐   ┌────────────────────────┐   ┌─────────────────────┐
    │ SupervisorTui  │◄──┤     ToolRegistry       │◄──┤ JobManager &        │
    │ Event-driven,  │   │ (52 authoritative      │   │ WorkflowEngine      │
    │ virtualized    │   │  ToolDefinitions)      │   │ (DAG, locks, retry) │
    └────────────────┘   └──┬──────┬──────┬────┬──┘   └──────────┬──────────┘
                            │      │      │    │                 │
         ┌──────────────────┘      │      │    └────────┐        │
         ▼                         ▼      ▼             ▼        ▼
 ┌───────────────┐  ┌───────────────┐  ┌──────────────┐ ┌───────────────────┐
 │ WorkspaceGuard│  │ Roslyn + Text │  │ ProcessRunner│ │ StateDatabase     │
 │ Confinement & │  │ Code Intel    │  │ + TunnelMgr  │ │ (SQLite WAL) +    │
 │ Write Locks   │  │ (Bounded LRU) │  │ + Win32 Job  │ │ Streaming LogStore│
 └───────────────┘  └───────────────┘  └──────────────┘ └───────────────────┘
```

## Core Subsystems

- **`Core/PortablePaths.cs` & `AppConfig.cs`** — Resolves portable root (`AppContext.BaseDirectory` or `--home` / `LOCALIDEBRIDGE_HOME`), manages atomic temp-file-and-rename config saves, and isolates all persistent state inside the application directory.
- **`Core/CredentialStore.cs` & `RedactingLogger.cs`** — 256-bit key lifecycle, constant-time SHA-256 comparison, ACL hardening, streaming rotated logs, and fixed-size `BoundedRingBuffer<ActivityEvent>`.
- **`Core/WorkspaceGuard.cs`** — Path normalization, per-segment NTFS junction / symlink target validation, per-repo `.localidebridge.json` enforcement, and per-workspace `SemaphoreSlim(1,1)` write serialization.
- **`Storage/StateDatabase.cs` & `LogStore.cs`** — SQLite in WAL mode (`cache_size = -2048` = 2 MB page cache cap, `temp_store = FILE`, non-pooled for immediate file release). Stores sessions, tool calls, jobs, incremental job events, edit journal metadata, Git checkpoints, workflows, and log line indexes. Command logs and edit backup snapshots live on disk under `data/sessions/<sessionId>/` and are streamed by line range on demand.
- **`Execution/WindowsJobObject.cs` (`ChildProcessSupervisor`), `ProcessRunner.cs`, & `TunnelManager.cs`** — P/Invokes `CreateJobObjectW` + `SetInformationJobObject` (`JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`) and `AssignProcessToJobObject` on Windows 11, paired with explicit `Process.Kill(entireProcessTree: true)` tracking. Extracts MSBuild/Roslyn diagnostics and parses VSTest `.trx` results.
- **`Tools/*` & `ToolRegistry.cs`** — Single source of truth for all 52 tools. MCP `tools/list`, `/api/v1/tools`, `/openapi.json`, `cli schema`, and `docs/TOOLS.md` (`gendocs`) are all produced directly from `ToolDefinition` instances.
- **`Workflows/JobManager.cs` & `WorkflowEngine.cs`** — Bounded-concurrency background job execution and cycle-checked DAG orchestration (parallel independent read-only steps via `SemaphoreSlim`, sequential write steps under workspace lock, `runIf` predicates, safe retries, per-step timeouts, durable SQLite & Git checkpoints).
- **`Protocols/McpEndpoint.cs` & `HttpApiEndpoints.cs`** — Standards-compliant MCP Streamable HTTP (`POST` JSON/SSE, `GET` SSE with `Last-Event-ID`, `DELETE` session teardown, compatible with both session-bearing `2025-xx` and stateless `2026-07-28` specs), local `mcp-stdio`, and REST/OpenAPI 3.1.
- **`Cli/SupervisorTui.cs`, `CliClient.cs`, `SetupWizard.cs`, `BenchmarkAndDocs.cs`** — Alternate-screen ANSI supervisor sleeping on a `SemaphoreSlim` signal between events, agent CLI client, first-run wizard, and empirical benchmark harness.

## Process & Crash Recovery Lifecycle

1. **Startup:** Ensures portable directories, cleans stale `temp/`, opens SQLite, transitions any `queued`/`running` jobs or workflows left behind by a prior crash into `interrupted_restart` (never auto-replaying non-idempotent commands), creates a fresh session, prunes expired logs/sessions, binds Kestrel, writes `data/host.pid`, and optionally starts a Job-Object-owned tunnel.
2. **Normal shutdown (`Q`, `Ctrl+C`, `DisposeAsync`):** Stops tunnel -> stops Kestrel and frees TCP port -> cancels in-flight jobs -> terminates all owned child process trees and closes Job Object handle -> removes `data/host.pid` -> truncates SQLite WAL checkpoint and closes file handles.
3. **Forced termination / crash:** Because Kestrel runs in-process, the MCP/HTTPS endpoint vanishes the instant the OS process exits. On Windows 11, the NT kernel closes the last handle to the Job Object and immediately kills every assigned child command, build, test runner, and tunnel subprocess.

## Extension Points

To add a new tool, yield a `ToolDefinition` in the appropriate `Tools/*.cs` module and register it in `WorkbenchHost`. It automatically becomes discoverable and callable via MCP Streamable HTTP, MCP stdio, `/api/v1/tools/{name}/invoke`, `/openapi.json`, `LocalIdeBridge cli`, `job_start`, `workflow_run`, and `LocalIdeBridge gendocs`.
