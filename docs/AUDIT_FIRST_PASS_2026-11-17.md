# RAMCA — first audited safety hardening pass (source only)

**Baseline:** `1fc09f17b3d2410a265a24f5c5469a0489d8867f`

This change is **an incremental review branch**, not a certified release. The full frontier audit found four blockers and eight critical findings, and most remain open. Do **not** deploy for unattended access to important projects yet.

## Changes included

- **F-001 (partial):** The host now acquires an exclusive filesystem lock before startup recovery, temp cleanup, or writes to live state. A second host on the same portable folder fails closed. `instructions` now reads only configuration instead of constructing a second host, so it can run while the listener owns the folder. A simultaneous `mcp-stdio` command must not create another host; use the primary server's HTTP MCP transport instead. Cross-platform lock behavior still needs a real Windows test.
- **F-002 (containment):** `rename_file` refuses to overwrite any existing destination even if `overwrite=true`. A reversible multi-file rename transaction is **not** implemented; moves to unused destinations are still not journaled as a group.
- **F-003:** `git_worktree remove` no longer passes `--force`; Git normally refuses removal of modified or untracked worktrees. Test this on Windows.
- **F-006 (containment):** Text file editing rejects invalid UTF-8 and UTF-16 bytes instead of silently replacing bytes with `U+FFFD`. Legacy CP1252/Latin-1 files need a future explicit encoding workflow. Full-file binary NUL scanning replaces the 8 KiB prefix check.
- **F-009 (partial):** Pausing mutation now leaves `job_cancel`, `workflow_cancel`, and `revert_edit` callable. Build-side effects mislabeled as read-only still require correction.
- **F-010:** Each SSE idle waiter is deregistered after its keepalive deadline or disconnect, preventing waiter saturation; a stream beyond capacity closes rather than busy-spins. A long-duration concurrency stress test is still required.
- **F-020:** CLI `--workspace` overrides now pass `WorkspaceGuard.ValidateTrustableDirectory` before being trusted.
- **F-021 (partial):** `connection.useHttps=true` without a configured/generated certificate now refuses to listen rather than serving plaintext. Certificate EKU/SAN and live-client compatibility still require fixes.
- **F-022:** Gemini CLI Streamable HTTP template now uses `httpUrl`.

## New tests

`tests/LocalIdeBridge.Tests/AuditP0SafetyRegressionTests.cs` contains five targeted regression tests: second-host state isolation, protected rename overwrite, malformed legacy encoding, HTTPS fail-closed, and cancellations/recovery available while paused.

## Not fixed — still blocks production use

- **F-004:** Multi-file `apply_patch` is not crash-atomic and can misapply hunks or lose change sections.
- **F-005 / F-030:** Directory symlinks, Git internals, denied-path policy and NTFS aliases still need canonical confinement.
- **F-007:** The command runner needs executable policy and environment hardening.
- **F-008:** Multi-project `dotnet_test` output and build locks can misreport failures.
- **F-011:** Global mutable active workspace can redirect concurrent/queued edits.
- **F-012:** Browser-based ChatGPT and Claude MCP compatibility (OAuth + platform permissions) remains unverified/unimplemented.
- **F-013 / F-014 / F-023 / F-024:** Key rotation and auth throttling, cancellation/idempotency, and workflow retries need redesign.
- **F-015..F-019:** Durable relative paths, journal retention, large result storage, log recovery and bounded process output are unfinished.
- **All other open findings** from `audit/03-findings-register.md` remain to be triaged.

## Validation requirements

This source was patched in an environment **without the .NET SDK**. Test code is authored but not executed here. It must undergo `dotnet restore`, `dotnet build`, full unit tests, and adversarial regression/fault-injection tests on disposable Windows workspaces **before merge into main**.

The complete audit evidence lives in the user's `ramca-audit-1fc09f1.zip`, not in this source patch. Keep the audited branch and baseline available for rollback.
