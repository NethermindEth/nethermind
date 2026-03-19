# Benchmark CV Reduction: Real RocksDB + BDN Standardization

**Date**: 2026-03-19
**Status**: Approved
**PR**: #10855 (feature/block-processing-newpayload-benchmarks)

## Problem

Both `BlockProcessingBenchmark` and `NewPayloadBenchmark` use in-memory `MemDb` instead of real RocksDB. This means they don't test realistic production I/O behavior. Additionally, `NewPayloadBenchmark` uses manual `Stopwatch` timing instead of BenchmarkDotNet, producing inferior statistics. Current FlatState CVs are 20-40%, making benchmark comparisons unreliable.

Root cause chain:
- `TestEnvironmentModule` always registers `MemDbFactory`, overriding any RocksDb configuration
- `BlockProcessingBenchmark.GlobalSetup` calls `TestMemDbProvider.Init()` which forces `DiagnosticMode.MemDb`
- `NewPayloadBenchmark` inherits `TestBlockchain` which loads `TestEnvironmentModule`

## Goals

1. Both benchmarks use real RocksDB on disk
2. Both benchmarks use BenchmarkDotNet for statistical rigor
3. CV under 5% for most scenarios, under 10% for heavy I/O scenarios

## Design

### 1. BenchmarkEnvironmentModule

A new DI module in `Nethermind.Evm.Benchmark` that replaces `TestEnvironmentModule` for benchmark contexts.

**Responsibilities:**
- Registers `RocksDbFactory` pointing at a temp directory (`Path.GetTempPath()/nethermind-bench-{guid}`)
- Exposes `Cleanup()` to delete the temp directory
- Configures RocksDB for benchmarking:
  - Block cache pinned at 256MB (large enough for working set, small enough to not mask I/O)
  - WAL sync disabled (no crash recovery needed)
  - Compaction style and levels matching production defaults
- Provides `FlushAndCompact()` and `DisableAutoCompaction()` / `EnableAutoCompaction()` via existing `ITunableDb` interface — cast `IDb` instances to `ITunableDb` and call `Tune(TuneType.DisableCompaction)` / `Tune(TuneType.Default)`. No new DB layer API needed. For flush/compact, call `Flush()` and `Compact()` on `DbOnTheRocks` via the `IDb` → `ITunableDb` cast path.

**Location:** `Nethermind.Evm.Benchmark/BenchmarkEnvironmentModule.cs`

### 2. Backend-Specific RocksDB Integration

The Trie and FlatState backends have fundamentally different database wiring. Each needs a different approach to swap in RocksDB.

#### Trie Backend

Currently: `TestMemDbProvider.Init()` creates `MemDb` instances. `TestWorldStateFactory.CreateWorldStateManagerForTest(dbProvider)` builds a trie-backed world state.

Change: Replace `TestMemDbProvider.Init()` with a `RocksDbProvider` created via `BenchmarkEnvironmentModule`. The `IDbProvider` interface is the same — `StateDb`, `CodeDb`, etc. — just backed by RocksDB instead of MemDb. `TestWorldStateFactory.CreateWorldStateManagerForTest` continues to work unchanged since it only depends on `IDbProvider`.

#### FlatState Backend

Currently: `BlockProcessingBenchmark` manually constructs `BenchmarkFlatDbManager` with `NoopPersistenceReader` — reads that miss all snapshots return nothing. No disk I/O.

Change: Replace `NoopPersistenceReader` with `RocksDbPersistence.Reader` (from `Nethermind.State.Flat/Persistence/RocksDbPersistence.cs`). This requires creating a `RocksDbPersistence` backed by a real `IColumnsDb<FlatDbColumns>` from RocksDB. The `BenchmarkFlatDbManager` and `BenchmarkFlatWorldStateManager` classes stay but wire up the real persistence reader instead of the noop one.

For `NewPayloadBenchmark`: since it uses the full DI pipeline via `TestBlockchain`, the fix is simpler — replace `TestEnvironmentModule` with `BenchmarkEnvironmentModule` in `TestBlockchain.ConfigureContainer`. The DI container handles the FlatDb wiring through the standard module chain (`FlatDbModule` → `RocksDbPersistence`).

### 3. BDN Configuration

Shared configuration used by both benchmarks.

| Setting | Value | Rationale |
|---------|-------|-----------|
| Toolchain | InProcessNoEmitToolchain | Single process, simplifies RocksDB lifecycle |
| InvocationCount | 1 | Each invocation processes real blocks |
| UnrollFactor | 1 | No tight-loop repetition |
| LaunchCount | 1 | Single launch avoids re-creating RocksDB. Current value is 2 (line 106 of BlockProcessingBenchmark.cs) but with InProcessNoEmitToolchain this just reruns warmup+iteration cycles, not separate processes. Reducing to 1 and increasing IterationCount compensates. |
| WarmupCount | 3 | JIT warmup + RocksDB page cache warming |
| IterationCount | 10 | More samples for stable statistics with disk I/O noise (up from 5) |
| GcForce | true | Force GC between iterations to reduce allocation noise |
| Columns | Mean, Median, StdDev, Min, Max, P90, P95 | Standard reporting |
| GC mode | Server GC disabled, concurrent GC disabled | Matches single-core pinning, reduces GC thread interference |

CPU pinning to core 0 stays in `GlobalSetup`. Note: this only affects the main benchmark thread — RocksDB native background threads (flush, compaction) are unaffected by `ProcessorAffinity`. This is acceptable since we disable auto-compaction during measurement.

### 4. NewPayloadBenchmark Conversion to BDN

Current: static class with manual `Stopwatch` timing, `--newpayload` CLI entry point.
New: proper BDN benchmark class.

**Lifecycle:**
- `[GlobalSetup]`: Build chain A, produce all payloads (mixed transactions). Initialize `BenchmarkEnvironmentModule`. Also **seed and prepare a template chain B**: create RocksDB, seed genesis (1M accounts, 500k storage slots), replay 30 warmup blocks, flush and compact. Save as a RocksDB checkpoint (hard-link based, near-instant copy) to a template directory.
- `[IterationSetup]`: Restore chain B from the template checkpoint (copy hard-linked directory). Open RocksDB, call `DisableAutoCompaction()`. This avoids re-seeding 1M accounts per iteration — the expensive seeding happens once in `GlobalSetup`.
- `[Benchmark(OperationsPerInvoke = 50)]`: Replay 50 measured blocks. For each: call `engine_newPayloadV4` then `engine_forkchoiceUpdatedV3`. BDN measures the whole method, reports per-block average.
- `[IterationCleanup]`: `EnableAutoCompaction()`, dispose chain B, delete iteration DB directory.
- `[GlobalCleanup]`: Dispose chain A, delete template and temp directories.

**Parameters:** `[Params(StateBackend.Trie, StateBackend.FlatState)]` — new for this benchmark (currently uses `--backend` CLI arg).

**Block counts:** 30 warmup (in GlobalSetup template) + 50 measured per iteration. With 10 iterations = 500 measured blocks total (vs current 150).

**Dropped:**
- Manual `Stopwatch` timing
- `ReportStatistics` method (TrimmedMean, TrimmedCV, etc.)
- `--newpayload` CLI entry point
- `--blocks` / `--warmup` CLI arguments

### 5. BlockProcessingBenchmark Changes

Already BDN — changes are minimal.

**What changes:**
- Replace `TestMemDbProvider.Init()` with `BenchmarkEnvironmentModule` creating a `RocksDbProvider`
- For FlatState path: replace `NoopPersistenceReader` with `RocksDbPersistence.Reader` in `BenchmarkFlatDbManager`
- `GlobalSetup`: After building DI container and seeding state, call `FlushAndCompact()`
- New `[IterationSetup]`: Call `FlushAndCompact()` + `DisableAutoCompaction()`
- New `[IterationCleanup]`: `EnableAutoCompaction()`
- `GlobalCleanup`: Dispose RocksDB, delete temp directory
- Config: LaunchCount 2 to 1, IterationCount 5 to 10, WarmupCount 2 to 3

**What stays the same:**
- All 13 benchmark scenarios
- Pre-built blocks in GlobalSetup
- CPU pinning to core 0
- `OperationsPerInvoke` pattern (N_LARGE/N_MEDIUM/N_SMALL)
- Branch processor pattern (fresh branch from same parent per invocation)
- `[Params(StateBackend.Trie, StateBackend.FlatState)]`

Note: Unlike NewPayloadBenchmark, BlockProcessingBenchmark does NOT need per-iteration DB recreation. It processes the same pre-built block on a branched state from the same parent — the DB is not mutated destructively between iterations.

### 6. Workflow Changes

**`run-block-processing-benchmark.yml`:**
- Increase `timeout-minutes` from 120 to 180
- Add temp directory cleanup step before and after benchmark runs (`rm -rf /tmp/nethermind-bench-*`) to handle stale directories from crashed runs
- No other structural changes (already runs BDN with `--filter *BlockProcessingBenchmark*`)

**`run-new-payload-benchmark.yml`:**
- Replace 4 manual `dotnet run -- --newpayload` steps with single BDN run: `dotnet run -- --filter *NewPayloadBenchmark*`
- Drop `--blocks` / `--warmup` workflow inputs
- Replace custom stdout-parsing Python script with JSON-based parser (same as BlockProcessingBenchmark workflow)
- Comment format aligns with BlockProcessingBenchmark: Mean, Median, CV, P90, P95, Min, Max, allocation delta
- Increase `timeout-minutes` from 120 to 180
- Add temp directory cleanup step before and after benchmark runs
- **Tradeoff**: consolidating 4 separate runs into one means if one backend crashes, both results are lost. Accepted — BDN provides better isolation within a single run than manual Stopwatch across separate runs.

**`benchmark-dispatcher.yml`:** No changes.

### 7. CV Reduction Levers (ranked by expected impact)

1. **Flush + compact before measurement**: Eliminates variance from inconsistent LSM tree state. Biggest single CV reducer.
2. **Disable auto-compaction during measurement**: Prevents RocksDB background threads from stealing CPU and causing I/O contention mid-iteration. Eliminates random spike pattern.
3. **More iterations (5 to 10)**: Better outlier detection, tighter confidence intervals.
4. **Single launch (2 to 1)**: Eliminates inter-launch variance (different page cache states, warmup differences).
5. **GC tuning** (Server GC off, concurrent GC off, GcForce): Prevents unpredictable GC during measurement.
6. **CPU pinning** (already in place): Reduces OS scheduler jitter.
7. **WAL sync disabled**: Removes fsync variance on writes.

## Risks

1. **Runtime increase**: Real RocksDB + more iterations will increase total benchmark time. The NewPayloadBenchmark checkpoint approach mitigates the biggest cost (avoiding 1M account seeding per iteration). Estimated total: ~60-90 minutes per benchmark (within 180 min timeout for both).
2. **RocksDB handle leaks**: Opening/closing RocksDB multiple times in a single process (NewPayloadBenchmark IterationSetup/Cleanup cycles) risks native handle leaks. Mitigate by ensuring `Dispose()` is called on all DB instances and the shared `HyperClockCacheWrapper` is managed at GlobalSetup scope, not per-iteration.
3. **FlatState persistence wiring complexity**: Replacing `NoopPersistenceReader` with `RocksDbPersistence.Reader` in `BlockProcessingBenchmark` requires creating a `IColumnsDb<FlatDbColumns>` backed by RocksDB. This needs the `FlatDbColumns` column family definitions, which come from the FlatDb module chain. May require extracting column family setup into a reusable helper.

## Files to Create

- `Nethermind.Evm.Benchmark/BenchmarkEnvironmentModule.cs`

## Files to Modify

- `Nethermind.Evm.Benchmark/BlockProcessingBenchmark.cs`
- `Nethermind.Evm.Benchmark/NewPayloadBenchmark.cs`
- `.github/workflows/run-block-processing-benchmark.yml`
- `.github/workflows/run-new-payload-benchmark.yml`

## Expected Outcome

- Both benchmarks hit real RocksDB, testing realistic production behavior
- Both use BDN for standardized, statistically rigorous reporting
- Trie and FlatState CVs converge (both hit disk now)
- Target CV: under 5% for most scenarios, under 10% for heavy I/O
- Current 20-40% FlatState CVs eliminated
