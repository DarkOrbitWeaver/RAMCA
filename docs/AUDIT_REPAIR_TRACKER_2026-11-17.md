# RAMCA — independent audit repair tracker

**Baseline audited:** `1fc09f17b3d2410a265a24f5c5469a0489d8867f`  
**Source of findings:** uploaded `ramca-audit-1fc09f1.zip`, findings F-001 through F-038.  
**Repair state (2026-11-18):** subsequent source repairs are being committed directly on `main` per owner policy; **NOT RELEASED OR VALIDATED**. The statuses below describe the original audit pass and must be reconciled against the current source before sign-off.

The auditor's **5.6/10** was a rating of the original source, not the quality of the audit or a judgment of its author. Reported severity is guidance, not a substitute for verification.

## Shipping rule

- Never install or remotely expose this branch as a trusted tool bridge based on source changes alone.
- Every destructive regression must be exercised against **disposable** repositories and large/test fixtures.
- Require Windows .NET 10 clean restore, build, test, actual Windows 11 process/job tests, power-interruption simulation, authentic MCP client tests, secret handling and remote-access validation.
- Owner policy is `main` only, with small descriptive commits; no branches or worktrees without explicit authorization. Still require all release blockers to pass before deploying the portable binary.
- A disconnected webchat cannot automatically resume a model's internal reasoning: RAMCA can only preserve durable local execution state and produce handoff briefs.
- No tool can mathematically guarantee never losing data on uncontrolled hardware or arbitrary administrator-level shell code. Protect through backups, fail-closed operations, narrow permissions and independent restore tests.

## Historical engineering changes in the audit-repair source

1. Host lock before database recovery and temp cleanup; lightweight connection-instructions path; invalid CLI workspace overrides rejected.
2. File mutations: rename overwrite rejected; destructive worktree `--force` removed; large unbacked deletes refused even if `allowIrreversible=true`.
3. Text handling: strict encoding, line-ending preservation, safer temp files, and mode/ACL-conscious replacements.
4. Unified patch: duplicate targets rejected, SHA map preconditions, hunk count validation, unique-only fuzz, no-newline fail-closed, Git-prefix-pair validation to prevent wrong-file editing, full preflight and verified rollback with persisted transaction manifest.
5. **Single-file create/overwrite, replace-range and delete** now also record their recovery manifest *before mutation*, and verify checksum/SQLite journal *before marking committed*. Incomplete transactions are recovered at startup only if every target is a known pre/postimage; human changes are not overwritten.
6. Committed-but-not-removed manifests are safe to garbage-collect even after later authorized edits; cleanup failure does not retroactively undo a successful commit.
7. Workspace path safety: `.git` metadata access denied, traversal/reparse aliases rejected, source reads and background job execution pinned to the validated workspace; policy-denied paths filtered by workspace guard.
8. Execution: constrained executable origin, direct Git push policy, TLS secret removal from child environments, bounded stdout/stderr chunking, fail-closed Windows Job Object assignment, MSBuild node reuse disabled.
9. Jobs and workflows: workspace-carrying execution contexts, registration-race fix, idempotent-only retries, condition dependency checks, full JSON result spills and paginated retrieval, indexed in-flight logs.
10. Durable state: SQLite `synchronous=FULL`, never prune unreverted edit backups merely due count/session retention, portable backup/log lookup after app relocation.
11. Tests: all TRX files aggregated for a solution, absent or malformed test counters fail closed, incomplete status/diff stdout is reported explicitly rather than presenting a misleading clean Git state.
12. Transport: SSE waiter cleanup, HTTP method type guard, protocol-version header validation, valid bearer requests independent of failed-auth lockout, credential live-rotation check, TLS fail-closed and ServerAuth EKU improvements; connection instructions updated for Gemini HTTP MCP.
13. Added targeted regression tests to disposable workspaces for crash-recovery conflict rules, patch correctness, deletion safety, unpruned backups, Git metadata protection, large job-result fidelity, and TRX aggregation.
14. Added a durable `operation_ledger` with explicit `execute_once` and `operation_status` tools. An interrupted operation becomes `interrupted_unknown` at startup and is not automatically replayed; documented client contract in [Execution Continuity](EXECUTION_CONTINUITY.md). This is **opt-in** and does not yet deduplicate ordinary raw MCP calls.

### Additional continuity and protocol source hardening (2026-11-17)

- `revert_edit` now uses the same write-ahead transaction path as ordinary edits, and produces a journaled undo-of-undo operation. An uninterrupted Windows rebuild and real power-loss tests remain required.
- Recovery now locates pending transaction backups by portable session/edit IDs and streams large restores without buffering entire files in RAM.
- Modern MCP `2026-07-28` handling uses discovery and request metadata, rejects incompatible batches and routing headers, and avoids removed SSE event IDs on modern request-scoped streams. External client compatibility has NOT been proven.
- `execute_once` no longer delegates HTTP RequestAborted cancellation to the reserved operation. A client disconnect alone should not cancel an already-reserved tool call. The host itself must remain running. Added a regression test, unexecuted here.
- Background jobs distinguish timeout from user cancellation in durable status. Request-body limits now apply to chunked transfers through the HTTP server feature.
- GitHub CI was expanded to Windows and Linux, but its runs failed before exposing usable job steps; **a passing build and test suite has not been established**.

### Implementation != verification

The above statements describe **source changes**. As of this draft, no .NET SDK was available in the editing container; consequently **no new build or test has been independently executed there**. GitHub Actions has previously returned `failure` without usable job-step logs, so do not treat CI as green until a successful run can be inspected.

## Finding-by-finding status

Legend: **Implemented (unverified)** = source change intended to address, needs tests. **Partial** = some scenarios addressed, more engineering required. **Open** = not implemented.

| Finding | State | Remaining concern |
|---|---|---|
| F-001 second host corrupts state | Implemented (unverified) | Independent CLI/stdio concurrency lifecycle tests on Windows |
| F-002 rename overwrite data loss | Contained (unverified) | Overwrite rejected; ergonomic reversible rename/undo remains open |
| F-003 forced worktree remove | Implemented (unverified) | Dirty/untracked Windows worktree regression |
| F-004 multifile patch silent corruption | Partial | Real crash/failpoint injection, shell/editor races, executable-mode preservation |
| F-005 symlink/denied-path bypass | Partial | NTFS handle-level identity / Windows short names, security fuzz |
| F-006 invalid text encoding writes | Implemented (unverified) | Encoding fixtures and Windows file-share races |
| F-007 external commands escape policy | Partial | An authorized arbitrary shell still has full Windows user permissions, NOT a sandbox |
| F-008 TRX/project test accuracy | Implemented (unverified) | Multi-TFM/multiproject CI and abnormal test process exit |
| F-009 pause/build/test behavior | Partial | Pause running-workflow semantics, command safety |
| F-010 SSE busy spin | Implemented (unverified) | Reconnection/idle CPU load tests |
| F-011 mutable global workspace | Partial | Session ownership and branch identity sentinel still missing |
| F-012 browser MCP OAuth 2.1 | **Open — major interoperability blocker** | Secure OAuth/PKCE pairing and real ChatGPT connection not implemented |
| F-013 token rotation stale | Partial | Live rotation needs adversarial concurrent requests |
| F-014 failed-auth IP lockout | Implemented (unverified) | Rate-limit load tests / authenticated abuse isolation |
| F-015 portable log/backup refs | Implemented (unverified) | Full folder-relocation test incl. pending transactions |
| F-016 journal count prunes live undo | Implemented (unverified) | Disk quota, multi-user retention and verified recovery archive |
| F-017 invalid truncated job JSON | Implemented (unverified) | Spill write-failure handling & atomic job-state transaction |
| F-018 log starts before metadata | Implemented (unverified) | Crash-tail reconciliation |
| F-019 unbounded process output | Implemented (unverified) | Process, MCP stdio and tunnel streams bounded; test with malformed gigabyte streams |
| F-020 CLI workspace override | Implemented (unverified) | Windows path/casing and symlink test |
| F-021 TLS fallback/certificate | Partial | Full SAN cases, CA trust and remote real-client verification |
| F-022 Gemini MCP config | Implemented (unverified) | Live Gemini CLI connection |
| F-023 MCP cancel/validation/stdio | Partial | Invalid method guarded; MCP stdio framed; opt-in execute-once added; automatic MCP cancellation/deduplication and stdio HOL still open |
| F-024 workflow conditions/retry safety | Partial | Durable workflow state, pause/resume recovery and fan-out stress |
| F-025 symlink traversal loops | Implemented (unverified) | Very deep Windows junction test |
| F-026 Roslyn false positives | Open | Proper MSBuildWorkspace/project references/semantic validation |
| F-027 inaccurate git status/diff | Implemented (unverified) | Large repository benchmark / paginated diff UX |
| F-028 file metadata/ACL on replace | Partial | Windows ACL, ADS, signed-files, readonly attributes test |
| F-029 power-loss edits/SQLite | Partial | Crash manifests and fsync added; actual power-loss/volume-failure tests not done |
| F-030 NTFS ADS/alias | Partial | ADS/reserved names guarded; Windows 8.3, device paths remain for fuzzing |
| F-031 JobObject assignment race | Partial | Assignment failure kills child; suspended-start race-free launch unimplemented |
| F-032 inaccurate MCP tool annotations | Open | Audit all isReadOnly/idempotent/destructive hints |
| F-033 database per-call overhead | Open | Profile first; avoid persistent DB process if idle cost rises |
| F-034 unacknowledged local steering | Open | Acknowledged inbox and client capability negotiation |
| F-035 read/edit edge cases | Partial | Opaque encodings, long paths, empty/trailing-newline fixture coverage |
| F-036 asynchronous jobs race/timeouts | Partial | Job registration race fixed; timeout vs user cancel distinction open |
| F-037 tunnel process monitoring | Partial | Failed process supervision kills spawned tunnel; live restart/health checks and no-orphan Windows tests open |
| F-038 MCP redundant work/protocol | Open | Profile payload overhead and version negotiation tests |

## Remaining release-blocking acceptance tests

1. Compile .NET 10 source on clean **Windows** and Linux, run all baseline and new tests, fix every compiler/test failure.
2. Inject kill/power-loss at each edit stage (backup, manifest, rename, SQLite journal, commit marker); verify original bytes and no loss of later human edits.
3. Validate creation/deletion/rename including nested paths, Git worktrees, filenames with spaces, Unicode, read-only files, and Windows shares/ACLs.
4. Execute real MCP tools through a supported external client, including auth rotation, cancellation, network interruption and replay behavior.
5. Test a long job with connection lost then recovered; add idempotency ledger before attempting automatic side-effect retries.
6. Test branch/workspace changes from two clients, and ensure no stale operation executes in a different tree.
7. Verify child process termination on close/crash, Windows Job Object behavior and tunnel lifecycle (including kill of full process tree).
8. Establish secure OAuth connector compatibility for eligible ChatGPT/Claude clients **without exposing an anonymous remote code-execution endpoint**.
9. Measure bounded RAM/CPU for ≥10,000 operations and test log/result/undo retention under full disk.

**Bottom line:** These changes are a substantive but **incomplete** repair pass. RAMCA is NOT marked install-ready, remotely deployable or safe for unattended valuable-project access.
