# Outstanding Work & Known Limitations Register

In keeping with strict engineering honesty, this register distinguishes what was built and verified end-to-end from items that depend on running directly on a physical Windows 11 desktop or logged-in third-party SaaS accounts.

## Evidence reported by the original implementation

The following results come from the original author's bundled artifacts. They are useful reference material, but have **not been reproduced against the current revision**.

- Complete C# / .NET 10 implementation (`LocalIdeBridge.sln`, 0 compiler errors, 0 C# compiler warnings).
- Portable self-contained Windows 11 x64 single-file executable (`LocalIdeBridge.exe`) and portable ZIP release (`releases/LocalIdeBridge-win-x64-portable.zip`) containing the full portable directory hierarchy and documentation.
- All 52 tools across workspace inspection, Roslyn C# semantic code intelligence, atomic file editing/unified-diff patching/verified revert, command execution, `dotnet build`, `msbuild`, `dotnet test` (TRX structured counts & failures), Git (init, status, diff, branch, sandboxed worktree, stage, commit, checkpoint, policy-gated push), async jobs, paginated sessions, streaming log slices, and DAG workflows.
- Three universal interfaces over one shared execution engine: MCP Streamable HTTP (`/mcp`, supporting both session-bearing `2025-03-26..2025-11-25` and stateless `2026-07-28`) + local `mcp-stdio`, authenticated HTTP/HTTPS JSON API (`/api/v1/*`) + OpenAPI 3.1.0 (`/openapi.json`), and `LocalIdeBridge cli`.
- Independent MCP client verification via official `@modelcontextprotocol/sdk` `StreamableHTTPClientTransport` executing the full 10-step disposable C# workflow (`artifacts/mcp-independent-verify.txt`).
- 17/17 automated reliability & security integration tests passing (`artifacts/test-results.txt`).
- Real empirical 100 / 1,000 / 10,000 synthetic-operation memory, CPU, startup, build, session-switching, 10k-line log paging, and shutdown benchmarks (`artifacts/benchmark-results.json`).

## November 15, 2026 source-only review fixes (new, not yet runtime-tested)

- Updated and pinned SQLite dependencies to avoid the previously recorded affected transitive package.
- Protected `delete_file` against silent irreversible deletion when its backup would be omitted. Up to 256 MiB is now snapshotted and recoverable; larger files require `allowIrreversible=true`.
- `revert_edit` now verifies the saved backup SHA-256 against the original before restoring it.
- Browser Origin matching for a configured public endpoint now requires identical scheme, hostname and port (not hostname alone).
- Added regression tests for the above file-recovery and Origin behaviors; **these new tests were not run in the source-editing environment because it has no .NET SDK.**

**Still outstanding:** Full optional approval/review-before-commit/review-before-apply modes and safe selective reversal of overlapping edits. `revert_edit` remains a checked single-edit rollback, not a three-way merge tool. Browser-based ChatGPT/Claude MCP OAuth 2.1 is still not implemented, and an MCP option appearing in the UI does not guarantee the user's workspace permits custom write tools. No new Windows executable was built in this patch package.

## Host-Specific Verification Items to Confirm on Physical Windows 11

1. **Native Windows 11 kernel execution validation:** `LocalIdeBridge.exe` was cross-compiled from the official .NET 10.0.404 SDK targeting `win-x64` (`--self-contained true`). While all cross-platform code paths ran live on .NET 10, the Win32-only P/Invoke paths (`CreateJobObjectW` / `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`, `clip.exe` clipboard copy, `icacls` ACL restriction, NTFS `mklink /J` directory junctions, `ENABLE_VIRTUAL_TERMINAL_PROCESSING`, and Windows PowerShell `powershell.exe` / Visual Studio `msbuild.exe`) should be smoke-tested on a live Windows 11 machine (`.\LocalIdeBridge.exe doctor` and `.\LocalIdeBridge.exe benchmark`).
2. **Live vendor AI SaaS/CLI logins:** Compatibility configurations for OpenAI Codex CLI, Claude Code, Claude Desktop, Gemini CLI, and ChatGPT/Claude.ai connectors are documented from current official protocol specifications rather than live authenticated vendor sessions (no paid API keys or vendor accounts exist in the clean build environment). In particular, ChatGPT web Developer Mode MCP connectors require OAuth 2.1 rather than static Bearer tokens; Codex CLI/Desktop and OpenAPI Bearer Actions work directly with LocalIdeBridge's access key.
3. **SQLite advisory — source patched, rebuild needed:** Updated `Microsoft.Data.Sqlite` from `10.0.5` to `10.0.15` and pinned `SQLitePCLRaw.bundle_e_sqlite3 2.1.14`. Official NuGet metadata specifies a 2.1.13+ base for 10.0.15; the old transitive 2.1.11 was affected by `GHSA-2m69-gcr7-jv3q`. **Not yet confirmed by a fresh NuGet restore/audit**; run `dotnet restore` and `dotnet list ... package --vulnerable --include-transitive` on Windows.
4. **Roslyn cross-project NuGet symbol resolution:** In-process Roslyn tools (`go_to_definition`, `find_references`, `find_implementations`, `inspect_type`) compile workspace `.cs` files against core .NET BCL metadata references for fast, zero-MSBuild-host bounded memory (~4 MB cache cap). Workspace source symbols resolve with full semantic accuracy; diagnostics involving third-party NuGet types or source generators use `compiler_diagnostics` with default `mode="build"`.
5. **Secondary optional GUI/image features:** Secondary optional items from Section 14 (image paste, screenshot attachment, separate GUI companion) were intentionally omitted to keep the executable small, dependency-free, and focused on reliable supervisory TUI/CLI operation.
