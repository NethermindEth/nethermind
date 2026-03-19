# Benchmark CV Reduction Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Switch both BlockProcessingBenchmark and NewPayloadBenchmark from in-memory MemDb to real RocksDB on disk, convert NewPayloadBenchmark to BenchmarkDotNet, and reduce CV to <10%.

**Architecture:** Create a `BenchmarkEnvironmentModule` that registers `RocksDbFactory` instead of `MemDbFactory`. Both benchmarks use BDN with tuned iteration counts. RocksDB is flushed/compacted before measurement and auto-compaction is disabled during measurement to eliminate background I/O spikes.

**Tech Stack:** C#/.NET, BenchmarkDotNet, RocksDB (via Nethermind.Db.Rocks), Autofac DI, GitHub Actions workflows

---

## File Map

| File | Action | Responsibility |
|------|--------|---------------|
| `Nethermind.Evm.Benchmark/BenchmarkEnvironmentModule.cs` | Create | DI module: registers RocksDbFactory, provides FlushAndCompact/DisableAutoCompaction helpers, manages temp directory lifecycle |
| `Nethermind.Evm.Benchmark/BlockProcessingBenchmark.cs` | Modify | Replace MemDb with RocksDB via BenchmarkEnvironmentModule, add IterationSetup/Cleanup, update BDN config |
| `Nethermind.Evm.Benchmark/NewPayloadBenchmark.cs` | Rewrite | Convert from static+Stopwatch to BDN benchmark class with proper lifecycle |
| `Nethermind.Evm.Benchmark/Program.cs` | Modify | Remove `--newpayload` branch, just run BDN |
| `.github/workflows/run-block-processing-benchmark.yml` | Modify | Increase timeout, add temp dir cleanup |
| `.github/workflows/run-new-payload-benchmark.yml` | Rewrite | Replace manual runs+parser with BDN JSON-based comparison |

## Reference Files (read but don't modify)

- `Nethermind.Db.Rocks/DbOnTheRocks.cs` — `Flush()` at line 1370, `Compact()` at line 1384, `ITunableDb.Tune()` at line 1518
- `Nethermind.Db.Rocks/RocksDbFactory.cs` — Two constructors: line 22 takes `IRocksDbConfigFactory`, `IDbConfig`, `IInitConfig`, `HyperClockCacheWrapper`, `ILogManager`; line 28 takes explicit `string basePath` instead of `IInitConfig`
- `Nethermind.Db.Rocks/Config/RocksDbConfigFactory.cs` — Constructor at line 12: requires `IDbConfig`, `IPruningConfig`, `IHardwareInfo`, `ILogManager`, optional `bool validateConfig`
- `Nethermind.Db.Rocks/HyperClockCacheWrapper.cs` — Constructor at line 12: takes `ulong capacity` (default 32MB)
- `Nethermind.Init/Modules/DbModule.cs` — Line 40: registers `RocksDbFactory` by default; line 74: creates `HyperClockCacheWrapper` from `IDbConfig.SharedBlockCacheSize`; line 80: overrides with `MemDbFactory` when `DiagnosticMode.MemDb`
- `Nethermind.Init/Modules/FlatWorldStateModule.cs` — Line 87: `.AddColumnDatabase<FlatDbColumns>(DbNames.Flat)`; line 88: `.AddSingleton<RocksDbPersistence>()`
- `Nethermind.Core.Test/Db/TestMemDbProvider.cs` — `Init()` builds a DI container with `DbModule(new InitConfig { DiagnosticMode = MemDb })`
- `Nethermind.Db/ITunableDb.cs` — `TuneType.DisableCompaction`, `TuneType.Default`
- `Nethermind.Db/IDbProvider.cs` — `StateDb`, `CodeDb`, etc.
- `Nethermind.Core.Test/Modules/TestEnvironmentModule.cs` — Line 42: registers `MemDbFactory`, line 32: class declaration
- `Nethermind.Core.Test/Blockchain/TestBlockchain.cs` — Line 244: adds `TestEnvironmentModule` in `ConfigureContainer`
- `Nethermind.State.Flat/Persistence/RocksDbPersistence.cs` — `RocksDbPersistence(IColumnsDb<FlatDbColumns> db)`
- `Nethermind.State.Flat/FlatDbColumns.cs` — Column enum: Metadata, Account, Storage, StateNodes, StateTopNodes, StorageNodes, FallbackNodes

---

### Task 1: Create BenchmarkEnvironmentModule

**Files:**
- Create: `src/Nethermind/Nethermind.Evm.Benchmark/BenchmarkEnvironmentModule.cs`

This module replaces `TestEnvironmentModule` for benchmark contexts. It registers `RocksDbFactory` instead of `MemDbFactory` and provides helpers to control RocksDB behavior during measurement.

- [ ] **Step 1: Create the module file**

Create `BenchmarkEnvironmentModule.cs` with:

```csharp
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using Autofac;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Modules;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Db.Rocks;
using Nethermind.Db.Rocks.Config;
using Nethermind.Logging;

namespace Nethermind.Evm.Benchmark;

/// <summary>
/// DI module that replaces <see cref="TestEnvironmentModule"/> for benchmark contexts.
/// Registers <see cref="RocksDbFactory"/> backed by a temp directory instead of <see cref="MemDbFactory"/>.
/// Provides helpers for flushing/compacting and disabling auto-compaction during measurement.
/// </summary>
public sealed class BenchmarkEnvironmentModule : Module
{
    private readonly string _basePath;
    private readonly PrivateKey _nodeKey;

    public BenchmarkEnvironmentModule(PrivateKey? nodeKey = null)
    {
        _basePath = Path.Combine(Path.GetTempPath(), $"nethermind-bench-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_basePath);
        _nodeKey = nodeKey ?? TestItem.PrivateKeyA;
    }

    public string BasePath => _basePath;

    protected override void Load(ContainerBuilder builder)
    {
        // IMPORTANT: Registration order matters — Autofac uses last-registration-wins.
        // 1. Add TestEnvironmentModule FIRST (registers MemDbFactory + all test plumbing)
        // 2. Then override IDbFactory with RocksDbFactory SECOND (replaces MemDbFactory)
        builder.AddModule(new TestEnvironmentModule(_nodeKey, Random.Shared.Next().ToString()));

        // Override MemDbFactory with RocksDbFactory.
        // RocksDbConfigFactory requires: IDbConfig, IPruningConfig, IHardwareInfo, ILogManager
        IDbConfig dbConfig = new DbConfig { SharedBlockCacheSize = 256UL * 1024 * 1024 };
        IPruningConfig pruningConfig = new PruningConfig();
        IHardwareInfo hardwareInfo = new TestHardwareInfo(); // from Core.Test — provides 1 GiB mock

        var configFactory = new RocksDbConfigFactory(dbConfig, pruningConfig, hardwareInfo, LimboLogs.Instance);
        var sharedCache = new HyperClockCacheWrapper(256UL * 1024 * 1024); // 256MB pinned block cache

        builder.AddSingleton<IDbFactory>(new RocksDbFactory(
            configFactory, dbConfig, sharedCache, LimboLogs.Instance, _basePath));
    }

    /// <summary>
    /// Flush and compact the key benchmark databases.
    /// Call this in setup to ensure consistent LSM state before measurement.
    /// Only flushes StateDb, CodeDb, and MetadataDb — these are the ones hit during block processing.
    /// </summary>
    public static void FlushAndCompact(IDbProvider dbProvider)
    {
        IDb[] dbs = [dbProvider.StateDb, dbProvider.CodeDb, dbProvider.MetadataDb];
        foreach (IDb db in dbs)
        {
            if (db is DbOnTheRocks rocksDb)
            {
                rocksDb.Flush();
                rocksDb.Compact();
            }
        }
    }

    /// <summary>
    /// Disable auto-compaction on all key databases. Call before measurement.
    /// Uses existing <see cref="ITunableDb.Tune(ITunableDb.TuneType)"/> API.
    /// </summary>
    public static void DisableAutoCompaction(IDbProvider dbProvider)
    {
        IDb[] dbs = [dbProvider.StateDb, dbProvider.CodeDb, dbProvider.MetadataDb];
        foreach (IDb db in dbs)
        {
            if (db is ITunableDb tunableDb)
                tunableDb.Tune(ITunableDb.TuneType.DisableCompaction);
        }
    }

    /// <summary>
    /// Re-enable auto-compaction on all key databases. Call after measurement.
    /// </summary>
    public static void EnableAutoCompaction(IDbProvider dbProvider)
    {
        IDb[] dbs = [dbProvider.StateDb, dbProvider.CodeDb, dbProvider.MetadataDb];
        foreach (IDb db in dbs)
        {
            if (db is ITunableDb tunableDb)
                tunableDb.Tune(ITunableDb.TuneType.Default);
        }
    }

    /// <summary>
    /// Delete the temp directory. Call in GlobalCleanup AFTER disposing IDbProvider.
    /// </summary>
    public void Cleanup()
    {
        try { Directory.Delete(_basePath, recursive: true); }
        catch { /* best effort on cleanup */ }
    }
}
```

**Key design decisions in this code:**
1. `TestEnvironmentModule` is added **first**, then `RocksDbFactory` is registered **second** — Autofac last-registration-wins means our `RocksDbFactory` overrides `TestEnvironmentModule`'s `MemDbFactory`
2. Uses the `RocksDbFactory(configFactory, dbConfig, sharedCache, logManager, basePath)` constructor (line 28 of `RocksDbFactory.cs`) which takes an explicit `string basePath` instead of `IInitConfig`
3. `RocksDbConfigFactory` requires `IDbConfig`, `IPruningConfig`, `IHardwareInfo`, `ILogManager` (line 12 of `RocksDbConfigFactory.cs`)
4. `HyperClockCacheWrapper` takes `ulong capacity` — use `256UL * 1024 * 1024` to avoid int overflow
5. Only flushes/compacts StateDb, CodeDb, and MetadataDb — these are the databases hit during block processing. Other DBs (BlocksDb, HeadersDb, etc.) are not performance-critical for these benchmarks
6. `TestHardwareInfo` — check that this exists in `Nethermind.Core.Test`. If not, create a minimal mock implementing `IHardwareInfo` with `MaxOpenFilesLimit = null`

- [ ] **Step 2: Verify the module compiles**

Run:
```bash
dotnet build src/Nethermind/Nethermind.Evm.Benchmark/Nethermind.Evm.Benchmark.csproj -c Release
```
Expected: Build succeeds. Fix any constructor signature mismatches with `RocksDbFactory`, `HyperClockCacheWrapper`, `RocksDbConfigFactory`, `DbConfig`, or `InitConfig`. Read the actual constructors in `Nethermind.Db.Rocks/RocksDbFactory.cs` and adjust.

- [ ] **Step 3: Commit**

```bash
git add src/Nethermind/Nethermind.Evm.Benchmark/BenchmarkEnvironmentModule.cs
git commit -m "feat(bench): add BenchmarkEnvironmentModule with RocksDB support"
```

---

### Task 2: Update BlockProcessingBenchmark — BDN Config + RocksDB for Trie Backend

**Files:**
- Modify: `src/Nethermind/Nethermind.Evm.Benchmark/BlockProcessingBenchmark.cs`

This task updates the BDN configuration and replaces MemDb with RocksDB for the Trie backend path. The FlatState path is handled in Task 3.

- [ ] **Step 1: Update the BDN config class**

In `BlockProcessingBenchmark.cs`, replace `BlockProcessingConfig` (lines 98-116):

```csharp
/// <summary>
/// 10 data points per benchmark (1 launch x 10 iterations, 3 warmup).
/// GcForce ensures a GC collection between iterations to reduce allocation noise.
/// </summary>
private class BlockProcessingConfig : ManualConfig
{
    public BlockProcessingConfig()
    {
        AddJob(Job.Default
            .WithToolchain(InProcessNoEmitToolchain.Default)
            .WithInvocationCount(1)
            .WithUnrollFactor(1)
            .WithLaunchCount(1)
            .WithWarmupCount(3)
            .WithIterationCount(10)
            .WithGcForce(true)
            .WithEnvironmentVariable("DOTNET_GCServer", "0")
            .WithEnvironmentVariable("DOTNET_gcConcurrent", "0"));
        AddColumn(StatisticColumn.Min);
        AddColumn(StatisticColumn.Max);
        AddColumn(StatisticColumn.Median);
        AddColumn(StatisticColumn.P90);
        AddColumn(StatisticColumn.P95);
    }
}
```

- [ ] **Step 2: Add BenchmarkEnvironmentModule field and IDbProvider field**

Add fields to the class (near line 149):

```csharp
private BenchmarkEnvironmentModule? _benchmarkModule;
private IDbProvider? _dbProvider;
```

- [ ] **Step 3: Replace MemDb with RocksDB in GlobalSetup — Trie path**

In `GlobalSetup()`, replace the database creation block (line 229). The current code:
```csharp
IDbProvider dbProvider = TestMemDbProvider.Init();
```

Replace with a RocksDB-backed `IDbProvider`. The pattern mirrors `TestMemDbProvider.Init()` (see `Nethermind.Core.Test/Db/TestMemDbProvider.cs`) but uses `DiagnosticMode.None` instead of `DiagnosticMode.MemDb`, and sets `BaseDbPath`:

```csharp
_benchmarkModule = new BenchmarkEnvironmentModule();
IDbProvider dbProvider = new ContainerBuilder()
    .AddModule(new DbModule(
        new InitConfig { BaseDbPath = _benchmarkModule.BasePath },
        new ReceiptConfig(),
        new SyncConfig()
    ))
    .AddSingleton<IDbProvider, ContainerOwningDbProvider>()
    .Build()
    .Resolve<IDbProvider>();
_dbProvider = dbProvider;
```

`DbModule` with default `DiagnosticMode` (not `MemDb`) registers `RocksDbFactory` at line 40 of `DbModule.cs`. The `HyperClockCacheWrapper` is created from `IDbConfig.SharedBlockCacheSize` at line 74. Set `SharedBlockCacheSize` to 256MB by also registering a custom `IDbConfig`:

```csharp
.AddSingleton<IDbConfig>(new DbConfig { SharedBlockCacheSize = 256UL * 1024 * 1024 })
```

Add this line before `.Build()`. You'll also need `using Nethermind.Init.Modules;`, `using Nethermind.Api;`, `using Nethermind.Blockchain.Receipts;`, `using Nethermind.Blockchain.Synchronization;`.

At end of GlobalSetup, after seeding and verification, add:
```csharp
BenchmarkEnvironmentModule.FlushAndCompact(_dbProvider);
```

**Note:** With this approach, you may NOT need `BenchmarkEnvironmentModule` for the `IDbProvider` creation at all — `DbModule` handles everything. But you still need `BenchmarkEnvironmentModule` for the `FlushAndCompact`/`DisableAutoCompaction` helpers and temp directory lifecycle. Alternatively, simplify: put the temp path management directly in the benchmark class and use `DbModule` for DB creation. Choose whichever is cleaner.

- [ ] **Step 4: Add IterationSetup and IterationCleanup**

Add these methods after `GlobalCleanup`:

```csharp
[IterationSetup]
public void IterationSetup()
{
    if (_dbProvider is not null)
    {
        BenchmarkEnvironmentModule.FlushAndCompact(_dbProvider);
        BenchmarkEnvironmentModule.DisableAutoCompaction(_dbProvider);
    }
}

[IterationCleanup]
public void IterationCleanup()
{
    if (_dbProvider is not null)
        BenchmarkEnvironmentModule.EnableAutoCompaction(_dbProvider);
}
```

- [ ] **Step 5: Update GlobalCleanup to dispose RocksDB**

Replace `GlobalCleanup` (lines 381-386):
```csharp
[GlobalCleanup]
public void GlobalCleanup()
{
    _processingScope.Dispose();
    _container.Dispose();
    _dbProvider?.Dispose();
    _benchmarkModule?.Cleanup();
}
```

- [ ] **Step 6: Build and verify Trie backend works**

```bash
dotnet build src/Nethermind/Nethermind.Evm.Benchmark/Nethermind.Evm.Benchmark.csproj -c Release
dotnet run -c Release --project src/Nethermind/Nethermind.Evm.Benchmark/Nethermind.Evm.Benchmark.csproj \
  -- --filter "*BlockProcessingBenchmark.Transfers_200*" --job Dry
```

Expected: BDN runs a single dry-run iteration without errors. Verify that RocksDB files are created in `/tmp/nethermind-bench-*` and cleaned up after.

- [ ] **Step 7: Commit**

```bash
git add src/Nethermind/Nethermind.Evm.Benchmark/BlockProcessingBenchmark.cs
git commit -m "feat(bench): switch BlockProcessingBenchmark Trie backend to RocksDB"
```

---

### Task 3: Update BlockProcessingBenchmark — RocksDB for FlatState Backend

**Files:**
- Modify: `src/Nethermind/Nethermind.Evm.Benchmark/BlockProcessingBenchmark.cs`

The FlatState path manually constructs `BenchmarkFlatDbManager` with `NoopPersistenceReader`. Replace the noop reader with `RocksDbPersistence.Reader` backed by real RocksDB.

- [ ] **Step 1: Create RocksDB-backed FlatDb columns database**

In the FlatState branch of `GlobalSetup` (line 232), after creating the RocksDB `IDbProvider`, also create a `IColumnsDb<FlatDbColumns>` and `RocksDbPersistence` for the flat state persistence layer.

The production code registers these in `FlatWorldStateModule` (file: `Nethermind.Init/Modules/FlatWorldStateModule.cs`):
- Line 87: `.AddColumnDatabase<FlatDbColumns>(DbNames.Flat)` — creates the columns DB via `IDbFactory.CreateColumnsDb<FlatDbColumns>()`
- Line 88: `.AddSingleton<RocksDbPersistence>()` — constructor takes `IColumnsDb<FlatDbColumns>`

For the benchmark, create these manually:

```csharp
// Create FlatDb columns database backed by RocksDB
IDbFactory dbFactory = /* resolve from container or create directly */;
IColumnsDb<FlatDbColumns> flatColumnsDb = dbFactory.CreateColumnsDb<FlatDbColumns>(
    new DbSettings(DbNames.Flat, null));
RocksDbPersistence flatPersistence = new(flatColumnsDb);
```

Store as field:
```csharp
private RocksDbPersistence? _flatPersistence;
private IColumnsDb<FlatDbColumns>? _flatColumnsDb;
```

The `IDbFactory` can be resolved from the same container that creates `IDbProvider`, or you can use the `RocksDbFactory` instance directly. Check `DbNames.Flat` constant in `Nethermind.Db/DbNames.cs`.

- [ ] **Step 2: Replace NoopPersistenceReader in BenchmarkFlatDbManager**

Modify `BenchmarkFlatDbManager.GatherSnapshotBundle` (line 720-735) and `GatherReadOnlySnapshotBundle` (line 737-751) to use the real persistence reader instead of `NoopPersistenceReader`:

Replace:
```csharp
NoopPersistenceReader persistenceReader = new();
```
with:
```csharp
var persistenceReader = _persistence.CreateReader();
```

where `_persistence` is the `RocksDbPersistence` instance passed via constructor. Update the `BenchmarkFlatDbManager` constructor to accept an `IPersistence` parameter.

- [ ] **Step 3: Build and verify FlatState backend works**

```bash
dotnet build src/Nethermind/Nethermind.Evm.Benchmark/Nethermind.Evm.Benchmark.csproj -c Release
dotnet run -c Release --project src/Nethermind/Nethermind.Evm.Benchmark/Nethermind.Evm.Benchmark.csproj \
  -- --filter "*BlockProcessingBenchmark.Transfers_200*" --job Dry
```

Expected: Both Trie and FlatState parameters run without error. Check `/tmp/nethermind-bench-*` for RocksDB files.

- [ ] **Step 4: Commit**

```bash
git add src/Nethermind/Nethermind.Evm.Benchmark/BlockProcessingBenchmark.cs
git commit -m "feat(bench): switch BlockProcessingBenchmark FlatState backend to RocksDB"
```

---

### Task 4: Convert NewPayloadBenchmark to BDN

**Files:**
- Rewrite: `src/Nethermind/Nethermind.Evm.Benchmark/NewPayloadBenchmark.cs`
- Modify: `src/Nethermind/Nethermind.Evm.Benchmark/Program.cs`

Convert the static class with manual Stopwatch timing to a proper BDN benchmark class.

- [ ] **Step 1: Remove `--newpayload` branch from Program.cs**

Replace `Program.cs` contents:

```csharp
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using BenchmarkDotNet.Running;

namespace Nethermind.Evm.Benchmark;

public static class Program
{
    public static void Main(string[] args)
    {
        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
    }
}
```

- [ ] **Step 2: Rewrite NewPayloadBenchmark as BDN class**

Rewrite `NewPayloadBenchmark.cs` as a BDN benchmark class. The key structural changes:

1. Change from `public static class` to `public class` with `[Config]` and `[MemoryDiagnoser]`
2. Use same `BlockProcessingConfig` as BlockProcessingBenchmark (or a shared config), but with `OperationsPerInvoke = 50`
3. `[GlobalSetup]`: Produce all payloads on chain A. Seed and prepare template chain B, save as checkpoint.
4. `[IterationSetup]`: Restore chain B from checkpoint, disable auto-compaction.
5. `[Benchmark]`: Replay 50 measured blocks.
6. `[IterationCleanup]`: Dispose chain B, clean iteration DB.
7. `[GlobalCleanup]`: Dispose chain A, clean all temp dirs.

Key structural outline:

```csharp
[Config(typeof(NewPayloadConfig))]
[MemoryDiagnoser]
[JsonExporterAttribute.FullCompressed]
public class NewPayloadBenchmark
{
    private const int WarmupBlocks = 30;
    private const int MeasuredBlocks = 50;
    private const int TotalBlocks = WarmupBlocks + MeasuredBlocks;

    [Params(StateBackend.Trie, StateBackend.FlatState)]
    public StateBackend Backend { get; set; }

    private class NewPayloadConfig : ManualConfig
    {
        public NewPayloadConfig()
        {
            AddJob(Job.Default
                .WithToolchain(InProcessNoEmitToolchain.Default)
                .WithInvocationCount(1)
                .WithUnrollFactor(1)
                .WithLaunchCount(1)
                .WithWarmupCount(3)
                .WithIterationCount(10)
                .WithGcForce(true)
                .WithEnvironmentVariable("DOTNET_GCServer", "0")
                .WithEnvironmentVariable("DOTNET_gcConcurrent", "0"));
            AddColumn(StatisticColumn.Min);
            AddColumn(StatisticColumn.Max);
            AddColumn(StatisticColumn.Median);
            AddColumn(StatisticColumn.P90);
            AddColumn(StatisticColumn.P95);
        }
    }

    // ── Fields ──
    private PayloadData[] _payloads = null!;
    // Chain B for measurement — recreated per iteration
    private BenchmarkMergeBlockchain _replayChain = null!;
    // Template DB checkpoint path
    private string _templateDbPath = null!;

    [GlobalSetup]
    public async Task GlobalSetup()
    {
        // Pin CPU
        if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
            Process.GetCurrentProcess().ProcessorAffinity = new IntPtr(1);

        // Phase 1: Produce payloads on chain A
        _payloads = await ProducePayloads(TotalBlocks, Backend);

        // Phase 2: Create template chain B — seed, replay warmup, checkpoint
        _templateDbPath = await CreateTemplateChain(Backend, _payloads, WarmupBlocks);
    }

    [IterationSetup]
    public void IterationSetup()
    {
        // Restore chain B from checkpoint
        _replayChain = RestoreFromCheckpoint(_templateDbPath, Backend);
        // Disable auto-compaction during measurement
        // (access IDbProvider from the chain and call DisableAutoCompaction)
    }

    [Benchmark(OperationsPerInvoke = MeasuredBlocks)]
    public async Task ReplayBlocks()
    {
        IEngineRpcModule rpc = _replayChain.EngineRpcModule;
        for (int i = WarmupBlocks; i < _payloads.Length; i++)
        {
            ExecutionPayloadV3 p = _payloads[i].Payload;
            await rpc.engine_newPayloadV4(p, Array.Empty<byte[]?>(),
                p.ParentBeaconBlockRoot, _payloads[i].ExecutionRequests);
            await rpc.engine_forkchoiceUpdatedV3(
                new ForkchoiceStateV1(p.BlockHash, p.BlockHash, p.BlockHash));
        }
    }

    [IterationCleanup]
    public void IterationCleanup()
    {
        _replayChain?.Dispose();
        // Clean up iteration-specific DB directory
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        // Clean up template and all temp directories
    }

    // Keep existing helper methods: ProducePayloads, BuildMixedTransactions,
    // BuildEip1559Tx, SeedGenesis, BuildChainConfigurer, CreateOsakaSpecProvider,
    // BenchmarkMergeBlockchain, PayloadData record
}
```

The implementation details for `CreateTemplateChain` and `RestoreFromCheckpoint` depend on how RocksDB checkpoints work in Nethermind. The key idea:
- `CreateTemplateChain`: Build chain B with RocksDB, seed genesis, replay warmup blocks, flush+compact, create RocksDB checkpoint (hard-link copy) to a template directory.
- `RestoreFromCheckpoint`: Copy the template directory to a fresh iteration directory, open as new chain B.

If RocksDB checkpoints aren't easily accessible, a simpler alternative is: copy the entire DB directory (works but slower for large DBs). For the benchmark's DB size (~1M accounts), directory copy should be fast enough.

- [ ] **Step 3: Remove deleted code**

Remove from `NewPayloadBenchmark.cs`:
- `ReportStatistics` method (lines 440-477)
- `Percentile` method (lines 479-486)
- `Run` static method (lines 83-119)
- `Replay` static method (lines 207-261)
- CLI argument parsing (lines 84-103)
- All `Console.WriteLine` progress reporting

- [ ] **Step 4: Build and verify**

```bash
dotnet build src/Nethermind/Nethermind.Evm.Benchmark/Nethermind.Evm.Benchmark.csproj -c Release
dotnet run -c Release --project src/Nethermind/Nethermind.Evm.Benchmark/Nethermind.Evm.Benchmark.csproj \
  -- --filter "*NewPayloadBenchmark*" --job Dry
```

Expected: BDN runs a dry-run iteration for both backends without errors.

- [ ] **Step 5: Commit**

```bash
git add src/Nethermind/Nethermind.Evm.Benchmark/NewPayloadBenchmark.cs \
        src/Nethermind/Nethermind.Evm.Benchmark/Program.cs
git commit -m "feat(bench): convert NewPayloadBenchmark to BenchmarkDotNet with RocksDB"
```

---

### Task 5: Update run-block-processing-benchmark.yml

**Files:**
- Modify: `.github/workflows/run-block-processing-benchmark.yml`

- [ ] **Step 1: Increase timeout and add cleanup**

At line 40, change:
```yaml
    timeout-minutes: 120
```
to:
```yaml
    timeout-minutes: 180
```

Add a cleanup step after "Check out PR branch" (after line 45) and before "Build PR branch":
```yaml
      - name: Clean up stale benchmark temp dirs
        run: rm -rf /tmp/nethermind-bench-*
```

Add the same cleanup step as the last step:
```yaml
      - name: Clean up benchmark temp dirs
        if: always()
        run: rm -rf /tmp/nethermind-bench-*
```

- [ ] **Step 2: Commit**

```bash
git add .github/workflows/run-block-processing-benchmark.yml
git commit -m "fix(bench): increase timeout and add temp dir cleanup for RocksDB benchmarks"
```

---

### Task 6: Rewrite run-new-payload-benchmark.yml

**Files:**
- Modify: `.github/workflows/run-new-payload-benchmark.yml`

- [ ] **Step 1: Replace workflow with BDN-based approach**

The current workflow runs 4 separate `dotnet run -- --newpayload` invocations and parses stdout. Replace with a BDN-based approach matching the block-processing workflow.

Key changes:
1. Remove `blocks` and `warmup` inputs (lines 15-32) — hardcoded in BDN config now
2. Replace 4 manual run steps (lines 66-126) with 2 BDN runs (PR and base):
```yaml
      - name: Run benchmark on PR branch
        id: run-pr
        continue-on-error: true
        run: |
          dotnet run -c Release --no-build \
            --project ${{ env.BENCHMARK_PROJECT }} \
            -- --filter "*NewPayloadBenchmark*" \
               --exporters json \
               --artifacts "${RUNNER_TEMP}/bdn-pr"
```
3. Replace the custom stdout-parsing Python script (lines 147-231) with the JSON-based parser from `run-block-processing-benchmark.yml` (lines 133-305). Copy that Python script verbatim but change the marker to `<!-- newpayload-benchmark-report -->` and the title to `### engine_newPayload Benchmark Comparison`.
4. Increase timeout to 180 minutes.
5. Add temp dir cleanup steps (same as Task 5).

- [ ] **Step 2: Commit**

```bash
git add .github/workflows/run-new-payload-benchmark.yml
git commit -m "feat(bench): rewrite NewPayload workflow to use BDN JSON comparison"
```

---

### Task 7: End-to-End Validation

- [ ] **Step 1: Run BlockProcessingBenchmark locally with a small filter**

```bash
dotnet run -c Release --project src/Nethermind/Nethermind.Evm.Benchmark/Nethermind.Evm.Benchmark.csproj \
  -- --filter "*BlockProcessingBenchmark.Transfers_200*"
```

Expected: BDN produces results for both Trie and FlatState backends. Check:
- CV values in the output (target: <10%)
- RocksDB files created and cleaned up in `/tmp/nethermind-bench-*`
- No errors in console output

- [ ] **Step 2: Run NewPayloadBenchmark locally with a small filter**

```bash
dotnet run -c Release --project src/Nethermind/Nethermind.Evm.Benchmark/Nethermind.Evm.Benchmark.csproj \
  -- --filter "*NewPayloadBenchmark*"
```

Expected: BDN produces results for both backends. Verify OperationsPerInvoke reports per-block averages.

- [ ] **Step 3: Verify temp directory cleanup**

```bash
ls /tmp/nethermind-bench-* 2>/dev/null && echo "FAIL: stale dirs" || echo "PASS: clean"
```

Expected: "PASS: clean"

- [ ] **Step 4: Commit any final fixes**

```bash
git add -A
git commit -m "fix(bench): final adjustments from end-to-end validation"
```

---

## Implementation Notes

### Simplest Path: Use DbModule Directly

The simplest way to get a RocksDB-backed `IDbProvider` is to use `DbModule` with default `DiagnosticMode` (not `MemDb`). `DbModule` already registers `RocksDbFactory` (line 40), `HyperClockCacheWrapper` (line 74), `RocksDbConfigFactory` (line 39), and all standard databases. The only thing you need to set is `InitConfig.BaseDbPath` to your temp directory. See Task 2 Step 3 for the concrete pattern.

This means `BenchmarkEnvironmentModule` may end up being just a helper class with static methods + temp dir management, rather than a full Autofac Module. Choose whichever approach is cleaner.

### RocksDbConfigFactory Constructor

`RocksDbConfigFactory` (line 12 of `Config/RocksDbConfigFactory.cs`) requires:
- `IDbConfig dbConfig` — use `new DbConfig { SharedBlockCacheSize = 256UL * 1024 * 1024 }`
- `IPruningConfig pruningConfig` — use `new PruningConfig()`
- `IHardwareInfo hardwareInfo` — check `Nethermind.Core.Test` for `TestHardwareInfo` or a mock. If it doesn't exist, create a minimal `IHardwareInfo` implementation with `MaxOpenFilesLimit = null` and `AvailableMemoryBytes = 1_073_741_824` (1 GiB)
- `ILogManager logManager` — use `LimboLogs.Instance`

### HyperClockCacheWrapper

Constructor takes `ulong capacity` (line 12 of `HyperClockCacheWrapper.cs`). Default is 32MB. Use `256UL * 1024 * 1024` for 256MB. This is a `SafeHandle` — it wraps a native RocksDB cache and must be disposed.

### FlatState RocksDB Integration

The production code wires FlatState persistence in `FlatWorldStateModule` (file: `Nethermind.Init/Modules/FlatWorldStateModule.cs`):
- Line 87: `.AddColumnDatabase<FlatDbColumns>(DbNames.Flat)` — creates columns DB
- Line 88: `.AddSingleton<RocksDbPersistence>()` — wraps the columns DB

For the benchmark's manual wiring:
1. Create `IColumnsDb<FlatDbColumns>` via `IDbFactory.CreateColumnsDb<FlatDbColumns>(new DbSettings(DbNames.Flat, null))`
2. Create `RocksDbPersistence(flatColumnsDb)`
3. Pass the persistence to `BenchmarkFlatDbManager` — add an `IPersistence` constructor parameter
4. In `GatherSnapshotBundle`/`GatherReadOnlySnapshotBundle`, replace `new NoopPersistenceReader()` with `_persistence.CreateReader()`
5. Don't forget to flush/compact the FlatDb columns database too

### NewPayloadBenchmark Checkpoint Strategy

For `IterationSetup`, the simplest approach:
1. In `GlobalSetup`: after seeding + warmup, copy the entire DB directory to a template path
2. In `IterationSetup`: copy template to a fresh iteration path, open new chain from it
3. In `IterationCleanup`: delete iteration path

If copying is too slow (measure it), use RocksDB's native `Checkpoint` API — search for `CreateCheckpoint` in `DbOnTheRocks.cs`.

### Semantic Change: NewPayload Timing Includes forkchoiceUpdated

The original `NewPayloadBenchmark.Replay()` only timed `engine_newPayloadV4` (excluded `forkchoiceUpdatedV3`). The BDN conversion times the entire benchmark method, which includes both calls. This is intentional — BDN cannot separate timing within a method. The `forkchoiceUpdated` call is fast relative to `newPayload`, so the impact on reported numbers is small. Document this change in the PR description.

### Known Gotcha: IDbProvider Disposal Order

`IDbProvider.Dispose()` disposes all child databases. Make sure this is called BEFORE deleting the temp directory — otherwise RocksDB may fail to close cleanly. Order: dispose IDbProvider → dispose HyperClockCacheWrapper → delete temp directory.

### Existing Project References

The benchmark project (`Nethermind.Evm.Benchmark.csproj`) already references `Nethermind.Db.Rocks` (line 25) and `Nethermind.State.Flat` (line 24). No csproj changes needed.
