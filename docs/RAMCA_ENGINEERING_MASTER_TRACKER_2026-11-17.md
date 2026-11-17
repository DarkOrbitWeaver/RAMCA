# RAMCA — audited engineering implementation ledger (2026-11-17)

> **Source of truth for this development pass.** Based on independent Audits A, B and C at `daf5d5cec8082f207bd7c3ca781f6be50585e939`. Track only verified source changes and actual Windows test results. Personal tool, not commercial software. All work on `main`; no branches, worktrees, or PRs.

## Baseline
- Baseline Git revision: `daf5d5cec8082f207bd7c3ca781f6be50585e939`.
- Windows: Release `-warnaserror` build clean; `74/74` existing .NET tests passed. Disposable clone: `portable/sandbox/test/RAMCA-ENGINEERING-20261117`. Neither the running portable binary nor VESPER were modified.
- This ledger is a checklist, **not** a claim that every item is complete. Do not mark a finding resolved without a code change, case-specific regression test, and validation record.

## Master findings from Audit B
| Finding | Brief requirement | Status |
|---|---|---|
| F-01 | Zero-context patch coordinates | [ ] Pending verification/implementation |
| F-02 | Preview and SHA guards | [ ] Pending verification/implementation |
| F-03 | Rollback external-save race | [ ] Pending verification/implementation |
| F-04 | Dynamic edit/build write fence | [ ] Pending verification/implementation |
| F-05 | Safe retry eligibility | [ ] Pending verification/implementation |
| F-06 | Global failure-handler DAG deadlock | [ ] Pending verification/implementation |
| F-07 | Nested invocation ownership | [ ] Pending verification/implementation |
| F-08 | Checkpoint receipts | [ ] Pending verification/implementation |
| F-09 | Range I/O and strict budget | [ ] Pending verification/implementation |
| F-10 | Bounded multi-file reads | [ ] Pending verification/implementation |
| F-11 | Compact nonduplicate outputs | [ ] Pending verification/implementation |
| F-12 | Workflow dataflow and parallel reads | [ ] Pending verification/implementation |
| F-13 | Bounded job queue and waiting | [ ] Pending verification/implementation |
| F-14 | Exact call/session joins | [ ] Pending verification/implementation |
| F-15 | Session baseline/net diff | [ ] Pending verification/implementation |
| F-16 | Historical edit attribution | [ ] Pending verification/implementation |
| F-17 | Roslyn solution awareness | [ ] Pending verification/implementation |
| F-18 | Efficient truthful search | [ ] Pending verification/implementation |
| F-19 | Transactional audit records | [ ] Pending verification/implementation |
| F-20 | Retention and active-job pins | [ ] Pending verification/implementation |
| F-21 | Modern MCP validation | [ ] Pending verification/implementation |
| F-22 | Stateless request collision | [ ] Pending verification/implementation |
| F-23 | Process start/ownership race | [ ] Pending verification/implementation |
| F-24 | Tool probe timeout | [ ] Pending verification/implementation |
| F-25 | Git status/diff machine parsing | [ ] Pending verification/implementation |
| F-26 | Owner main-only policy | [ ] Pending verification/implementation |
| F-27 | TRX and test verification | [ ] Pending verification/implementation |
| F-28 | Large multi-file patch staging | [ ] Pending verification/implementation |
| F-29 | Canonical operation hash | [ ] Pending verification/implementation |
| F-30 | Build server optional benchmark | [ ] Pending verification/implementation |
| F-31 | Log truncation truthfulness | [ ] Pending verification/implementation |
| F-32 | Misleading config switches | [ ] Pending verification/implementation |
| F-33 | Tool discovery context overhead | [ ] Pending verification/implementation |
| F-34 | Independent MCP verifier fixture | [ ] Pending verification/implementation |
| F-35 | Undo/journal atomic linkage | [ ] Pending verification/implementation |
## Additional Audit A / C checks
| Finding | Requirement | Status |
|---|---|---|
| A-R-03 | Committed handler outcome preserved on audit insert failure | [ ] |
| A-G-02 | Preserve owner's already-staged Git changes | [ ] |
| C-COR-01 | Allow normal dot-relative paths | [ ] |
| C-COR-02 | Accept unborn Git HEAD | [ ] |
| C-COR-05 | Parse Git porcelain status accurately | [ ] |
| C-COR-08 | Job ownership, shutdown and queue bounds | [ ] |
| C-COR-10 | Reversible, attributable rename | [ ] |
| C-COR-11 | Session/workspace consistency | [ ] |
| C-COR-14 | Toolchain probe and unused dependency control | [ ] |
| C-COR-15 | Reliable tunnel restart/connection status | [ ] |
| C-INT-01 | Generic identifier references | [ ] |
| C-RES-01 | Measure SQLite open/PRAGMA overhead | [ ] |

## Milestones / acceptance gates
- [ ] **M1 — protect bytes.** Patches, previews, atomic rollback, conflicts, snapshots, crash injection, reverse-edit linkage.
- [ ] **M2 — execution ownership.** Session pinning, workflow/cancellation semantics, bounded admission, reconciliation, idempotency.
- [ ] **M3 — Git and protocol.** Correct Git parsing, safe staging/main-only, MCP transport compatibility, HTTP multi-client tests.
- [ ] **M4 — agentic throughput.** Batch source/range reads, compact bounded responses, step dataflow, incremental job waits, efficient project discovery.
- [ ] **M5 — session totals.** Verified, color-coded baseline-to-now totals (not sum of edit activity); historical partial/unknown disclosure; no redraw-time full Git scans.
- [ ] **M6 — measured optimization.** Windows before/after latency, model-facing bytes, calls, memory, CPU, process starts; no speculative caches.
- [ ] **M7 — final sign-off.** Windows full test suite, independent MCP SDK verifier, recovery fault injection, docs consistent, owner portable executable **not** overwritten during live connection.

## Work log
- 2026-11-17: Baseline verified `74/74`, 0 build warnings/errors. Source review ongoing; do not infer all listed findings resolved.
