# RAMCA / LocalIdeBridge — Independent Frontier Engineering Audit

## Mission

You are an independent senior Windows systems, C#/.NET, protocol, security, reliability, UX, and performance engineer. Audit the entire **RAMCA** repository (application currently named `LocalIdeBridge`) as if a long-running, unattended AI coding session could otherwise destroy valuable work. Be adversarial about correctness, not theatrical about risk. Your job is to find failures, demonstrate them with evidence, and propose or implement well-scoped fixes without turning this two-person portable tool into a commercial platform.

**Repository:** https://github.com/DarkOrbitWeaver/RAMCA

This is a comprehensive investigation, not a superficial checklist or a feature-count contest. Inspect the actual code, configuration, dependencies, tests, and docs. Do not infer that a claim is implemented merely because a README says so. Reproduce problems where possible. Explicitly distinguish **verified**, **source-inspected**, **theoretical**, and **not tested**. Never invent passing tests or compatibility with a web AI platform you cannot access.

## Product contract

The user starts a tiny, portable Windows 11 C#/.NET 10 companion, authorizes one or more workspace folders, and gives a compatible AI agent connection details. The agent uses MCP, HTTP, or CLI tools to inspect/edit code, run builds and tests, manage Git, and continue long work without permission spam. The user watches activity, may steer or stop work, and can review/revert changes. All app-owned configuration/state/logs/cache/sandbox stay in one portable directory. No mandatory paid model API. Listener/tunnel/owned child processes must stop when the app closes or crashes. The application is for the user and a friend, each with their own installation; preserve KISS and low idle CPU/RAM.

## Audit process and deliverable discipline

1. Establish repository SHA, branch, dirty state, target frameworks, package versions, and true build/test baseline. Work on a new branch or disposable worktree. Never rewrite the user's branches or touch personal projects destructively.
2. Inventory every file and subsystem; cross-reference all 52 advertised tools against registered implementations, input schemas, output semantics, actual side effects, tests and documentation. Identify tools reachable but not safe or functional.
3. Construct an executable threat-and-failure model grounded in the actual code. Prioritize data loss, security boundary escape, broken connectivity, unrecoverable sessions, misleading success reports, and memory/resource leaks.
4. Run focused real tests and controlled fault injection on **disposable** repositories. Only use real Windows 11 tests where available; distinguish Linux tests from Windows-only functionality.
5. Produce an evidence ledger for each issue: file + precise line/symbol, reproduction, impact, severity, likelihood, affected scenarios, existing mitigation, proposed fix, tests, residual risk and verification status.
6. Suggest ambitious new capabilities only after correctness and preservation are addressed; keep enhancements measurable and optional where appropriate.
7. If authorized to implement, make focused commits with regression tests, preserve original behavior, maintain a complete unresolved register, and avoid broad rewrites without benchmark or correctness justification.

## Deep coverage requirements

### 1. Connectivity and client compatibility

- Validate actual MCP Streamable HTTP conformance, protocol negotiation, sessions, `initialize`, `tools/list`, `tools/call`, SSE reconnect behavior, timeouts, cancellation, JSON-RPC errors, retry behavior, HTTP status mapping, content types, protocol versions, and whether stdio mode behaves correctly.
- Validate HTTP/OpenAPI equivalence, consistency with MCP and CLI, JSON schemas, error codes, idempotency claims, paging, large requests and tool-discovery reliability.
- Test **actual** supported clients if credentials/entitlements are available: ChatGPT custom MCP, Claude web/desktop, Codex, Claude Code, Gemini CLI, compatible third-party tools. Determine if OAuth/OIDC, bearer tokens, tools with write permissions, user approvals, or special discovery endpoints are required. Do not claim that pasted instructions alone add tools to arbitrary websites (including Arena).
- Evaluate safe and simple options to bridge OAuth-requiring web clients without exposing an unauthenticated execution service. No paid account or bespoke commercial identity platform is required for basic local use.
- Test direct IP, NAT/CGNAT, IPv4/IPv6, DNS, TLS cert mismatch/expiry/rotation, proxies, offline network, captive portals, clock skew, public URLs, reverse proxies, and optional owned tunnel process lifecycle. Distinguish transport reconnect from **model session** resume.
- Does refreshing a webchat, reaching quota, switching models, or closing the browser result in a predictable resumable local record rather than duplicate writes? Precisely document what the server cannot guarantee about client-side reasoning continuity.

### 2. Crash-consistent operations and no-work-loss recovery

- Power loss, app crash, `taskkill /F`, process kill while writing, sudden network drop, host restart, OS restart, disk full, read-only disk, antivirus lock, file lock, SQLite WAL corruption, backup truncation, temporary-file orphaning, partial rename, failed atomic swap, race with editor/formatter or Git, disk quota exhaustion.
- Durable transaction boundaries and fsync/checkpoint ordering; exactly-once vs at-least-once semantics; durable job IDs; safe recovery of in-flight writes and commands; no blind replay of non-idempotent edits, commits, Git operations or external calls.
- How to reattach after disconnect: workspace identity, session IDs, event sequence cursors, idempotency keys, result handles, resume tokens, replay of *status*, lost event detection, monotonic ordering, duplicate suppression, retention policies, conflict handling and bounded backlog.
- Evaluate whether long-running jobs must stop when the host exits (yes), while preserving state so a user can understand and explicitly restart them safely after reopening. Never claim processes continue after shutdown.
- Build crash-harness tests with failpoints injected at write, rename, database commit, patch, backup and Git boundaries. Verify original data integrity and accurate state across restarts.

### 3. Undo, review, approvals and change provenance

- Audit `revert_edit`, snapshot size limits, backup integrity/hash checks, deletes, moves, renames, multi-file patches, file permissions/attributes, encodings, line endings, binary files, symlinks/junctions, large files, untracked files, generated outputs, overlapping edits and external changes after the agent's edit.
- Distinguish discard, undo one edit, revert one commit, restore a checkpoint, restore deleted files, and three-way merge. Never use reset/checkout/clean in a way that silently drops human changes.
- Propose a **default unattended mode**, optional **review before commit** and **review before apply**; ensure the latter two actually enforce approvals without deadlocking unrelated tasks. Reviewable changes should be grouped by meaningful task, not only raw file snapshots.
- Add a diff viewer with lazy loading, selective revert where safe, conflict previews, clear irreversible-operation labeling, and complete post-action journal. Ensure undo metadata retention and disk limits do not undermine advertised recovery.
- Test concurrent writer conflicts (agent vs editor, two connected agents, branch switch) and optimistic-lock failures. Preserve user changes above any AI convenience.

### 4. Autonomous engine and orchestration

- Audit workflow DAG validation for cycles, missing dependencies, conditional branching, skipped/failed nodes, retries, cancellation, terminal failures, partial success, short-circuit behavior, parallel read-only tasks, locks and deadlocks, fairness, priority, timeouts, bounded concurrency, long output, nested workflows, out-of-order events, shutdown races.
- Avoid treating a model as a perpetual daemon: local orchestration can run predeclared deterministic tasks, but a paused/ended AI webchat cannot autonomously invent new decisions without an available inference session.
- Provide truthful tool progress and observable state transitions. Ensure retry-safe operations have idempotency handling; forbid automatic repetition of operations that may have already committed.
- Ensure error explanations enable the agent to diagnose and continue, with fail-fast semantics for untrusted or ambiguous operations.

### 5. Workspace boundary and local execution safety

- Audit canonicalization, ADS, case-folding, NTFS junctions, symlinks, hardlinks, mount points, UNC paths, drive-relative names, `..`, 8.3 aliases, paths changed after validation, TOCTOU, Git submodules, generated scripts and escaped working directories.
- Explicitly test command execution outside the trusted workspace through PowerShell, cmd, Git aliases, MSBuild target scripts, package install hooks, response files, environment variables and child processes. A current working directory is not a sandbox.
- The desired policy is *broad unattended engineering access inside user-selected folders*, not constant dialogs. Specify narrow exceptional cases needing approval: external folders, elevation, OS settings, secrets, irreplaceable personal data, broad destructive actions, remote push/publish unless approved.
- Distinguish security of remote requests from execution of untrusted build scripts; propose lightweight OS-level containment options rather than claiming impossible isolation.

### 6. API keys and internet-facing security

- Test key generation entropy, storage/ACL, plaintext leakage, logs/exception traces, screenshot/clipboard risks, header parsing, rotation while jobs run, timing behavior, token revocation, excessive permissions, HTTP origin checks, DNS rebinding, CORS and CSRF, reverse-proxy forwarded headers, localhost bypasses and remote TLS requirements.
- Rate limiting must be bounded under many IPs and resilient to proxy changes; inspect failed-auth lockouts, request size limits with chunked encoding, long-running SSE connections, JSON recursion, path parsing and maximum tool output.
- Test malformed MCP tool arguments, injection via repository text, prompt-injection against agent instructions, hostile Git config, secret exfiltration via outgoing commands, and accidental publication of private logs.
- Preserve a simple, portable personal app. Prioritize concrete exploit paths and proportional fixes over enterprise security theater.

### 7. Editor-grade code intelligence and edits

- Verify real Roslyn semantics across multi-project solutions, generated sources, project references, NuGet dependencies, conditional symbols, `global using`, partial declarations, symlinks, renamed files, stale caches, huge repos, mixed encodings, C# versions, SDK upgrades and source generators.
- Compare tool outputs against Visual Studio/`dotnet` compiler diagnostics. If project-wide symbol resolution is incomplete, clearly label the limitation.
- Audit unified diff parser: context matching, path traversal, absolute paths, CRLF preservation, patch ordering, partial patches, duplicate hunks, binary data, large files, concurrent edits, atomicity, rollback and expected hash behavior.
- Test all file operation tools for non-destructive failure handling and concise structured results.

### 8. Windows process lifecycle

- Test normal close, Ctrl+C, taskkill, crash, Windows logoff, sleep, resume, hibernation, fast startup, network interface change, Ctrl+Break and forced process termination. Verify the listener/socket and **all owned child processes/tunnels** die when the app does.
- Inspect Windows Job Objects, nested jobs, breakaway flags, inherited handles, process-tree lifetime, blocked/paused child processes, console ctrl handling, stuck processes, exit races and resource exhaustion.
- Ensure no startup entries, scheduled tasks, persistent services or unexpected host processes are left behind. Inspect temp/cache/log cleanup, portability when folder is moved, and multiple simultaneous instances.

### 9. Resource economy and long-lived sessions

- Measure cold start, steady idle CPU, memory after 100/1k/10k/100k synthetic operations, peak during builds, session switching, multi-agent sessions, reconnect storms, 10 GB logs, many small files, 100k-file repos, cancellation and GC pressure.
- Inspect SQLite growth/vacuum/WAL checkpointing, bounded history, lazy pagination, TUI rendering/virtualization, event subscriptions, `Task` and `CancellationTokenSource` disposal, connection fan-out, streaming backpressure and memory leak risks.
- Provide reproducible baselines and traces, not assumptions. Compare native AOT only if it really improves measurable startup/RAM without sacrificing functionality.

### 10. Local supervision, steer and multimodal feedback

- Design low-friction **supervisor actions**: pause, resume, cancel, reprioritize, approve exceptional operation, reject a proposed edit, review diff, undo, inspect failure, copy incident bundle.
- Investigate an optional small text input to **steer work mid-session**, plus clipboard text, images, screenshots and files. Distinguish what can be delivered to the live AI session via an official client integration from what can only be queued locally for a later tool poll.
- Propose `supervisor_events` / `supervisor_inbox` or notification/event cursor tools, with delivery acknowledgments, ordering and explicit *seen by agent* state. Prevent steering messages from silently disappearing on disconnect.
- Consider client polling vs MCP notifications where clients support them, and design degraded behavior for web clients that do not. Do not pretend a local text box can inject a message into any browser chat.
- Add command shortcuts in a TUI without becoming a full IDE. The UI should stay minimal, keyboard friendly, high contrast, resize-safe and non-flickering. User should see what changed, why a task is blocked, and how to regain control.

### 11. More high-value capabilities to evaluate (do not implement blindly)

- Session checkpoints with human-readable handoff briefs after quota exhaustion; cross-model continuation where the next agent can inspect current diffs, tests, job status and explicit pending decisions without trusting a giant transcript.
- Agent task leases/heartbeats and ownership handoff to avoid two models editing the same files simultaneously.
- Content-addressed snapshots and deduplicated recovery bundles with retention quotas and verified restore.
- Disaster-recovery export/import of the portable folder with secrets excluded by default; restore to a different PC/path; integrity manifest for logs and snapshots.
- One-click diagnostic bundle with secret redaction and reproducible run metadata.
- Automatic selection of focused tests based on changed files, but only as an optimization; important regression gates must remain explicit.
- Git-integrated checkpoints that are not destructive to dirty worktrees, and safe merge/conflict assistance.
- Connection health widget differentiating AI client status, local host, tunnel, network, authorization and tool protocol negotiation.
- Batch tool requests that reduce AI token/tool-call overhead, with bounded output, dry-run and explicit partial-result semantics.
- Tool capability negotiation and version compatibility so clients know what is unsupported; feature flags with graceful downgrade.
- Detect moved/replaced repositories and branch changes, preventing tools from editing stale workspace identities.
- Optional per-workspace resource budgets and protection against accidental infinite retry loops or runaway builds.
- Accessibility, screen-reader-friendly TUI output, copyable logs and non-visual status.
- Extension architecture for future browser debugging/Windows UI automation that does not inflate the idle process.
- Security-sensitive edit detection (e.g. secrets/config) with targeted user review rather than approval spam.

### 12. Packaging and maintainability

- Audit `config.example.json` and first-run wizard for correctness, defaults, schema upgrade, independent friend installations, generated key rotation, offline use and simple ZIP portability.
- Validate all docs against actual code and commands. Ensure the project name RAMCA and existing executable name LocalIdeBridge are explained consistently.
- Check dependency supply chain, outdated or vulnerable NuGet packages, pinned versions, reproducible release build, checksums, license notices, GitHub Actions/CI, Windows smoke tests and clean-room installation.
- Evaluate duplicate code, defensive clutter, overly broad catch blocks, ambiguous error messages, unowned temporary files and dead code. Improve clarity without “humanifying” merely by hiding AI provenance.

## Mandatory scenario matrix

Build a table with *trigger → expected behavior → actual behavior → evidence/test → result → fix priority* for, at minimum: remote disconnect during edit, disconnect during compile, reconnect with same job ID, reconnect with different AI client, duplicate `tool/call`, two agents editing same file, human editing during agent operation, kill during atomic file rename, crash between backup write and database commit, disk full at snapshot, deleted file over backup cap, corrupted rollback snapshot, Git commit succeeded but response lost, cancel while process spawns children, host crash with tunnel running, Windows sleep/resume, token reset mid-session, token leaked and revoked, invalid TLS certificate, blocked port, CGNAT, agent quota exhausted, TUI closed while host runs, multi-workspace path escape, symlink swap race, malicious repository instructions, repeated build failures, long log pagination, low disk space, enormous repo, 10k+ events, stale Roslyn cache, build without SDK, missing Git, update installation over existing state.

Add additional scenarios you discover; seek failure modes that a normal happy-path demo would miss.

## Deliverables

1. **Executive scorecard** (0–10 with transparent evidence) for: correctness/data integrity, recoverability, unattended operation, MCP compatibility, API/CLI usability, Windows lifecycle, security, code intelligence, TUI/steering, performance, documentation/maintainability.
2. **Source-anchored findings register**, sorted by severity: BLOCKER / CRITICAL / HIGH / MEDIUM / LOW / ENHANCEMENT. Do not exaggerate theoretical risks or declare an issue fixed without running its test.
3. **Traceable capability inventory** covering every advertised tool, with implementation, tests, side effects and confidence.
4. **Fault-injection and end-to-end testing report**, including exact commands, environment, logs, measured metrics and what was not tested.
5. **Concrete prioritized engineering roadmap**: P0 no-loss/security/connectivity; P1 recovery/agent continuity/review; P2 performance/UX/tool quality; P3 optional enhancements. Estimate complexity and regression risk; favor KISS.
6. **Creative feature proposals**, each with user story, technical design, dependency on client capabilities, memory/CPU impact, implementation effort and acceptance test.
7. **If explicitly authorized to implement**: tested code changes in isolated commits, before/after measurements, updated documentation, and a complete remaining-work register. Never push or publish without authorization.

## Definition of done

The goal is not “no theoretical bugs”—that cannot be guaranteed. The goal is a **defensible, independently verified personal coding tool** that preserves user work, reports honest results, resumes local state after failures, supports unattended engineering without permission hell, and gives any *compatible* agent clear ways to discover and use tools. Do not trade correctness, data safety or truthfulness for a feature count or impressive-looking reports.
