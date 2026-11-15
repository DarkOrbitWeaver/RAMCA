# Performance & Resource Measurements

LocalIdeBridge is engineered so memory does not scale with conversation length, session count, or completed tool invocations:

- **Workstation GC** (`ServerGarbageCollection=false`) keeps baseline heap reservations small.
- **SQLite WAL state (`data/state.db`)** bounded with `PRAGMA cache_size = -2048` (~2 MB page cache) and `PRAGMA temp_store = FILE`.
- **Streaming disk-backed logs (`data/sessions/<id>/logs/*.log`)** capped at `maxSingleLogBytes` (16 MB) and read by line range on demand (`read_log`).
- **Bounded LRU Roslyn syntax cache** capped at `64` entries / `4 MB` (`BoundedLruCache`).
- **Bounded ring buffers** for TUI activity events (`200` items) and SSE replay (`128` items), plus bounded edit journal backups (`250` items).
- **Event-driven TUI** sleeping on a `SemaphoreSlim` signal rather than spinning a polling loop.

## Real Measured Benchmarks

Raw machine-generated benchmark output is preserved in [`artifacts/benchmark-results.json`](../artifacts/benchmark-results.json) and [`artifacts/mcp-independent-verify.txt`](../artifacts/mcp-independent-verify.txt).

### Test Environment

- **Runtime:** .NET 10.0.15 (SDK 10.0.404), x64, Release build, Workstation GC (`serverGc: false`)
- **Hardware:** 4 vCPU Intel Xeon @ 2.60 GHz, 3.8 GiB RAM, SSD
- **Host OS note:** Measured directly on the .NET 10 Linux x64 CI sandbox (`Debian GNU/Linux 12`). Exact Windows 11 working set figures will differ slightly due to Windows SChannel/NT loader paging and Job Object handles; rerun the built-in command below on your Windows 11 PC for exact local numbers.

### Lifecycle, Idle, Build, Session, Log, and Shutdown Metrics

| Metric | Measured Value |
| --- | --- |
| In-process host init + Kestrel bind + `GET /health` 200 OK | **329.1 ms** |
| Full external subprocess cold start to `GET /health` 200 OK | **527 ms** |
| Idle working set (RSS, post-startup) | **90.01 MB** |
| Idle live managed heap | **1.23 MB** |
| Idle CPU (3.0 s quiet window across 4 vCPUs) | **0.258%** |
| Active `dotnet build` duration (disposable .NET 10 project) | **1,556.0 ms** |
| Host live managed heap after active build | **1.28 MB** |
| Host working set after active build | **134.82 MB** |
| Session switch + paginated `session_inspect` (25 sessions avg) | **11.56 ms** |
| Live managed heap after switching across 25 sessions | **1.30 MB** |
| Paginated log retrieval (10,000-line log, lines 1..100) | **2.00 ms** |
| Paginated log retrieval (10,000-line log, lines 9,901..10,000) | **8.15 ms** |
| Clean shutdown + immediate port release verification | **21.2 ms** (`listenerClosedImmediately: true`) |

### Synthetic Operation Scaling (100, 1,000, and 10,000 Real Tool Calls)

Workload mixes 5 real operations continuously (`read_range`, `text_search`, `repo_tree`, atomic `replace_range` with backup + SQLite edit journal, and `session_list`), auditing every invocation to `data/state.db` and `logs/`:

| Cumulative Ops | Throughput | Managed Heap (before GC) | Live Managed Heap (after GC) | Working Set (after GC) | `state.db` on Disk |
| ---: | ---: | ---: | ---: | ---: | ---: |
| **100** | 235.9 ops/s | 7.55 MB | **1.29 MB** | 94.77 MB | 0.14 MB |
| **1,000** | 263.0 ops/s | 7.21 MB | **1.31 MB** | 121.27 MB | 0.50 MB |
| **10,000** | 283.5 ops/s | 14.08 MB | **1.24 MB** | 134.28 MB | 3.14 MB |

**Key observation:** Increasing cumulative tool invocations **100x** (from 100 to 10,000, including 2,000 atomic file writes with reversible backups) left the **live managed heap essentially flat (`1.29 MB -> 1.31 MB -> 1.24 MB`)**. Historical records moved cleanly onto disk (`state.db` grew to `3.14 MB`), while native OS RSS stabilized around ~134 MB (JIT code pages + native SQLite page cache). Note that triggering Roslyn semantic compilation loads additional immutable metadata pages up to the configured LRU budget (`4 MB` cache cap plus BCL metadata references).

### Native AOT Evaluation

Native AOT (`PublishAot=true`) was evaluated and deliberately **not** enabled because `Microsoft.CodeAnalysis.CSharp` (Roslyn) is not trim/AOT-compatible without losing semantic C# code intelligence, and ASP.NET Core minimal API JSON serialization of dynamic tool payloads benefits from full reflection resilience. Instead, self-contained single-file publishing with Workstation GC and ReadyToRun provides sub-second cold start, flat managed memory, and full Roslyn compatibility in one portable `LocalIdeBridge.exe`.

## Reproducing Benchmarks on Windows 11

```powershell
.\LocalIdeBridge.exe benchmark --output .\benchmark-win11.json
```

This creates an isolated disposable project in `%TEMP%`, runs all measurements including the 100 / 1,000 / 10,000 operation sweep, prints the JSON report, and removes all temporary files automatically.
