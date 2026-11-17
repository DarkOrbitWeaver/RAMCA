# LocalIdeBridge

> **Safety notice (2026-11-17):** RAMCA is undergoing a high-priority recovery/security audit repair. The branch `fix/audit-p0-safety-first-pass` is **not an approved release**. Read [Audit Repair Tracker](docs/AUDIT_REPAIR_TRACKER_2026-11-17.md) before attempting remote access or use on valuable workspaces.


**Release status:** This repository contains source code and earlier test/benchmark artifacts. Security and recovery fixes made on November 15, 2026 have **not** been rebuilt or tested on Windows. Run the validation commands below before enabling remote access. See [Fixes and validation](FIXES_AND_VALIDATION.md) and [Outstanding work](OUTSTANDING_WORK.md).

**LocalIdeBridge** is a portable Windows 11 development-tool server written in C# and .NET 10. You choose a workspace and connect a compatible AI client through MCP, HTTP, or the command-line client. The same tools handle code navigation, editing, builds, tests, Git, and long-running jobs; a small terminal interface shows their progress.

This repository is published as **RAMCA**. The executable and C# namespaces retain the `LocalIdeBridge` name; renaming them requires a separate tested migration.

## Key Capabilities

- **Single portable folder:** `LocalIdeBridge.exe`, `config.json`, `data/` (SQLite WAL `state.db`, `secret.key`, `sessions/`), `logs/`, `cache/`, `temp/`, `sandbox/`, `tools/`, and `docs/`. Nothing written to `%APPDATA%`, `%PROGRAMDATA%`, or the Windows Registry.
- **No mandatory paid APIs, cloud accounts, or bundled LLM:** Provider-independent; works with any compatible MCP, HTTP, or CLI agent.
- **Three universal interfaces sharing one execution engine (52 tools):**
  1. **MCP Streamable HTTP** (`/mcp`) + local **MCP stdio** (`LocalIdeBridge.exe mcp-stdio`).
  2. **Authenticated HTTP/HTTPS JSON API** (`/api/v1/*`) + live **OpenAPI 3.1.0** schema (`/openapi.json`).
  3. **Lightweight CLI client** (`LocalIdeBridge.exe cli ...`) with structured JSON output and explicit exit codes.
- **Roslyn C# code intelligence + text search:** `symbol_search`, `go_to_definition`, `find_references`, `find_implementations`, `inspect_type`, `compiler_diagnostics`, `project_discover`, `dependency_inspect`, `text_search`, `regex_search` (semantic results are explicitly labeled distinct from text fallbacks).
- **Safe unattended editing:** SHA-256 stale-file detection, CRLF/LF and UTF-8 BOM/UTF-16 preservation, atomic temp-file replacement, multi-file unified diff application (`apply_patch`), dry-run `preview_edit`, and SHA-256-verified `revert_edit`.
- **Reliable process lifecycle:** Zero background services or startup tasks. Remote listener runs only while `LocalIdeBridge.exe` is open. Owned builds, tests, shells, and optional tunnels are bound to a Windows Job Object (`JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`) so normal exit, forced termination, or a crash terminates owned child processes cleanly.
- **Bounded memory & low idle CPU:** Workstation GC, bounded LRU syntax cache, fixed-capacity ring buffers, streaming disk-backed command logs, and paginated SQLite history (~1.24 MB live managed heap after 10,000 synthetic operations; see `docs/PERFORMANCE.md`).

## Validate before use

From the source directory on Windows with the .NET 10 SDK installed:

```powershell
dotnet restore LocalIdeBridge.sln
dotnet build LocalIdeBridge.sln -c Release --no-restore
dotnet test LocalIdeBridge.sln -c Release --no-build
```

Do not treat historical reports in `artifacts/` as proof that the current source passes. Remote ChatGPT/Claude web connections, OAuth and crash behavior need live client/Windows validation.

## System Requirements

- **Target OS:** Windows 11 x64 (self-contained `LocalIdeBridge.exe` requires no separate .NET runtime installation to run the host itself).
- **Optional developer tools on PATH (for project work):** Git, .NET SDK / MSBuild, PowerShell (`pwsh` or Windows PowerShell), plus optional `cloudflared.exe` or OpenSSH (`ssh.exe`) only if using tunnel mode.

## ChatGPT with one-time setup

RAMCA can manage the official OpenAI Secure MCP Tunnel automatically; press **C** in the TUI once to configure it, then simply start RAMCA on later uses. See [One-click ChatGPT connection](docs/ONE_CLICK_CHATGPT.md). This keeps the MCP server private and authenticated.

## Quick Start

1. Extract `LocalIdeBridge-win-x64-portable.zip` to any user-writable folder (for example `C:\Tools\LocalIdeBridge`).
2. Run `LocalIdeBridge.exe`.
3. Follow the 4-prompt terminal setup wizard (trust a workspace folder, verify detected dev tools, select connection mode, confirm port).
4. In the supervisor TUI:
   - Press **`E`** to copy the MCP endpoint URL.
   - Press **`K`** to copy the secret access key (masked on screen by default).
   - Press **`I`** to copy AI-readable connection instructions (secret excluded by default).
5. Configure your compatible MCP, HTTP, or CLI agent and start working. Press **`Q`** at any time to stop the listener and clean up all owned processes.

Scripted non-interactive setup is also built in:

```powershell
.\LocalIdeBridge.exe setup --workspace C:\Projects\MyApp --create-workspace --mode localhost --port 43170
.\LocalIdeBridge.exe host
```

## Important Limitations

- **Client compatibility:** Pasting instructions alone into a webchat window does **not** grant tool access. The AI client must support custom MCP servers, arbitrary authenticated HTTP requests, or shell execution with a network route to LocalIdeBridge. Web interfaces offering only fixed built-in tools are not compatible (see `docs/CONNECTING_AI_CLIENTS.md`).
- **Remote reachability:** PCs behind residential NAT or ISP CGNAT (`100.64.0.0/10`) are not directly reachable from cloud services without manual port forwarding + valid TLS or an optional owned tunnel (`docs/TROUBLESHOOTING.md`). LocalIdeBridge never silently alters router or firewall settings.
- **External tool caches:** LocalIdeBridge keeps all of its own state inside its portable folder, but third-party tools you invoke (`dotnet`, NuGet, `git`, `npm`, Windows itself) may still maintain their standard user-profile caches unless configured otherwise.
- **Shell confinement:** Workspace boundaries, symlink/junction checks, executable allowlists, and blocked admin patterns are enforced by the host, but an approved shell/compiler still runs under your Windows user account (not a full VM hypervisor). Only trust project folders and AI sessions you intend to run code in.
- **Git push:** Local commits proceed unattended; `git_push` is denied by default unless you set `autonomy.gitPushPolicy` to `"allow"` in `config.json`.

## Documentation Index

- [`docs/GETTING_STARTED.md`](docs/GETTING_STARTED.md) — First launch & complete disposable-project walkthrough
- [`docs/CONNECTING_AI_CLIENTS.md`](docs/CONNECTING_AI_CLIENTS.md) — MCP, HTTPS API, CLI, and client setup matrix
- [`docs/TOOLS.md`](docs/TOOLS.md) — Complete reference for all 52 tools, parameters, and error codes
- [`docs/CONFIGURATION.md`](docs/CONFIGURATION.md) — All `config.json` and `.localidebridge.json` settings
- [`docs/SECURITY.md`](docs/SECURITY.md) — Authentication, trust boundaries, credential rotation, and threat model
- [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) — Internal design, lifecycle, storage, and extension points
- [`docs/TROUBLESHOOTING.md`](docs/TROUBLESHOOTING.md) — NAT/CGNAT, TLS, auth, build, and shutdown diagnostics
- [`docs/PERFORMANCE.md`](docs/PERFORMANCE.md) — Real measured benchmarks (100 / 1,000 / 10,000 ops) and reproduction steps
- [`docs/AGENT_QUICKSTART.md`](docs/AGENT_QUICKSTART.md) — Copy-ready concise prompt reference for external agents
- [`OUTSTANDING_WORK.md`](OUTSTANDING_WORK.md) — Verification scope & remaining host-specific notes

## Independent audit

Use [`docs/FRONTIER_AUDIT_BRIEF.md`](docs/FRONTIER_AUDIT_BRIEF.md) for the comprehensive post-import audit, fault-injection plan, and agent-continuity requirements. The readability-only changes are summarized in [`HUMAN_READABILITY_PASS.md`](HUMAN_READABILITY_PASS.md).
