# EVM Gas Benchmarks

BenchmarkDotNet harness that replays real `engine_newPayloadV4` payloads from the [gas-benchmarks](https://github.com/NethermindEth/gas-benchmarks) submodule to measure EVM, block processing, block building, and newPayload throughput in **MGas/s**.

## Setup

```bash
# One-time: initialize the gas-benchmarks submodule (requires Git LFS)
git lfs install && git submodule update --init tools/gas-benchmarks
```

On Windows, enable long paths first if you encounter "Filename too long" errors:

```bash
git config --global core.longpaths true
```

If the submodule was already cloned without LFS (genesis file is ~130 bytes instead of ~53 MB):

```bash
git lfs install && cd tools/gas-benchmarks && git lfs pull
```

## Quick start

```bash
# Build once
dotnet build src/Nethermind/Nethermind.Evm.Benchmark -c Release

# Run a benchmark
dotnet src/Nethermind/artifacts/bin/Nethermind.Evm.Benchmark/release/Nethermind.Evm.Benchmark.dll \
  --mode=EVM --filter "*MULMOD*"
```

## Benchmark modes

Use `--mode=<name>` to select one processing path.

| Mode | Benchmark class | What it measures |
|---|---|---|
| `EVM` | `GasPayloadExecuteBenchmarks` | Transaction execution via `TransactionProcessor.Execute` (import path) |
| `BlockBuilding` | `GasBlockBuildingBenchmarks` | Producer path with `ProcessingOptions.ProducingBlock` |
| `NewPayload` | `GasNewPayloadBenchmarks` | Full `NewPayloadHandler` pipeline |
| `NewPayloadMeasured` | `GasNewPayloadMeasuredBenchmarks` | Instrumented newPayload with per-stage timing breakdown |

### Mode architecture

```
TX level:    EVMExecute ──→ BlockBuilding
                              ↓
Block level: BlockOne   ──→ Block ──→ NewPayloadMeasured ──→ NewPayload
```

- **EVM** — pure transaction execution; best for isolating opcode or precompile changes.
- **BlockBuilding** — block-producer flow including `ProducingBlock` options.
- **NewPayload** — closest to production `engine_newPayload` handling (JSON decode → handler → queue → processing).
- **NewPayloadMeasured** — same as NewPayload but instruments each stage (JSON parse, payload deserialize, TryGetBlock, sender recovery, block processing) with timing breakdown.

### Which mode to pick

| Changed project | Start with | Then |
|---|---|---|
| `Nethermind.Evm` / `Nethermind.Evm.Precompiles` | `EVM` | `BlockBuilding` |
| `Nethermind.Blockchain` / `Nethermind.State` / `Nethermind.Trie` | `BlockBuilding` | `NewPayload` |
| `Nethermind.Merge.Plugin` | `NewPayload` | `NewPayloadMeasured` |

## CLI reference

```bash
EXE=src/Nethermind/artifacts/bin/Nethermind.Evm.Benchmark/release/Nethermind.Evm.Benchmark.dll

# List available scenarios (no BDN harness)
dotnet $EXE --list-scenarios
dotnet $EXE --list-scenarios --filter "*extcode*"

# Run specific mode + pattern
dotnet $EXE --mode=EVM --filter "*MULMOD*"
dotnet $EXE --mode=BlockBuilding --filter "*a_to_a*"
dotnet $EXE --mode=NewPayloadMeasured --filter "*a_to_a*"

# Override BDN parameters
dotnet $EXE --mode=EVM --filter "*a_to_a*" --warmupCount 10 --iterationCount 10 --launchCount 1

# Chunked execution for CI parallelism (chunk 2 of 5)
dotnet $EXE --mode=NewPayload --chunk 2/5

# In-process mode (faster local iteration, less precise)
dotnet $EXE --inprocess --mode=EVM --filter "*a_to_a*"

# Diagnostic mode (single payload, no BDN, prints tx details)
dotnet $EXE --diag "opcode_MULMOD-mod_bits_63"
```

## Reading results

The output includes custom columns:

| Column | Description |
|---|---|
| **MGas/s** | `100M gas / mean_seconds / 1M` — higher is better |
| **CI-Lower / CI-Upper** | 99% confidence interval bounds for MGas/s |

A **regression** is a drop in MGas/s outside the confidence interval. If CI intervals of baseline and candidate overlap, the difference is not statistically significant.

For `NewPayload` and `NewPayloadMeasured`, timing breakdown reports are saved to `BenchmarkDotNet.Artifacts/results/`.

## Performance change workflow

1. Pick modes based on the code path you changed (see table above).
2. Run baseline: `--warmupCount 10 --iterationCount 10 --launchCount 1`.
3. Apply your change and rebuild.
4. Rerun with identical arguments.
5. Compare mean time, MGas/s, and allocations.
6. Add before/after numbers to your PR description.
7. Investigate any statistically meaningful MGas/s drop before merge.

## Benchmark invariants

- `BlocksConfig.PreWarmStateOnBlockProcessing = true` — block-level modes include prewarming overhead.
- `BlocksConfig.CachePrecompilesOnBlockProcessing = false` — avoids hiding precompile-specific deltas.
- **Avoid `--inprocess` for stable results.** In-process mode shares the GC/JIT with the BDN harness. Use it only for quick local iteration.
- Always compare baseline and candidate with identical arguments.

## Comparing with gas-benchmarks reference tool

`NewPayload` mode is the closest BDN equivalent to the [gas-benchmarks](https://github.com/NethermindEth/gas-benchmarks) reference tool, which runs Nethermind as a full node processing `engine_newPayloadV4` calls.

Trivially fast scenarios (e.g., TLOAD of uninitialized storage) may show large speedups (>10x) in BDN because the reference tool has fixed per-block overhead (networking, JSON-RPC, consensus) that dominates sub-millisecond workloads.

## Architecture and DI wiring

All benchmark classes use `BenchmarkContainer` to create Autofac DI containers with **production modules** (`BlockProcessingModule`, `WorldStateModule`, `DbModule` from `Nethermind.Init`). Benchmark-specific overrides (stub `IBlockTree`, `Always.Valid` block validator, genesis state) are layered on top.

| Component | Description |
|---|---|
| `BenchmarkContainer` | Central factory. `CreateTransactionScope()` for tx-level, `CreateBlockProcessingScope()` for block-level. |
| `BlockBenchmarkHelper` | Shared utilities: genesis header, setup payload execution, processing options. |
| `PayloadLoader` | Parses `engine_newPayloadV4` JSON files and loads genesis state. |
| `GasBenchmarkConfig` | BDN `ManualConfig` with chunking, MGas/s columns, and CLI overrides. |

Benchmark classes should **not** directly instantiate `EthereumTransactionProcessor`, `BranchProcessor`, or `BlockProcessor` — use the DI container.

## CI workflow

The `gas-benchmarks-bdn.yml` workflow:

1. **Build** — compiles the benchmark project and uploads artifacts.
2. **Benchmark** — runs in parallel chunks on `[self-hosted, benchmark]` runners.
3. **Collect** — merges chunk results, caches master baseline, posts PR comparison comments.

Triggers:
- **Push to `master`**: full suite, caches baseline for future PR comparisons.
- **PR labeled `performance is good`**: full suite, compares against cached master baseline.
- **`workflow_dispatch`**: configurable mode, filter, and BDN parameters.

## File listing

| File | Purpose |
|---|---|
| `GasBenchmarks/GasPayloadBenchmarks.cs` | Test case discovery and shared infrastructure |
| `GasBenchmarks/GasPayloadExecuteBenchmarks.cs` | EVM mode (`TransactionProcessor.Execute`) |
| `GasBenchmarks/GasBlockBuildingBenchmarks.cs` | BlockBuilding mode (producer path) |
| `GasBenchmarks/GasNewPayloadBenchmarks.cs` | NewPayload mode (`NewPayloadHandler` flow) |
| `GasBenchmarks/GasNewPayloadMeasuredBenchmarks.cs` | NewPayloadMeasured mode (instrumented timing) |
| `GasBenchmarks/BenchmarkContainer.cs` | DI container factory (production modules + overrides) |
| `GasBenchmarks/BlockBenchmarkHelper.cs` | Shared setup helpers |
| `GasBenchmarks/PayloadLoader.cs` | JSON payload parsing and genesis state loading |
| `GasBenchmarks/GasBenchmarkConfig.cs` | BDN configuration (jobs, columns, chunking) |
| `GasBenchmarks/GasBenchmarkColumnProvider.cs` | Custom MGas/s and CI columns |
| `Program.cs` | CLI entry point (`--mode`, `--chunk`, `--diag`, `--list-scenarios`) |
