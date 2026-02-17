## Changes

Add BenchmarkDotNet gas benchmarks with four modes (**EVM**, **BlockOne**, **Block**, **NewPayload**) that replay [gas-benchmarks](https://github.com/NethermindEth/gas-benchmarks) `engine_newPayloadV4` payload files, plus a GitHub Actions workflow for automated CI regression detection.

- **EVM mode** (`--mode=EVM`): Injects transactions directly into `TransactionProcessor.BuildUp` — measures pure EVM + transaction processing throughput
- **BlockOne mode** (`--mode=BlockOne`): Runs `BlockProcessor.ProcessOne` directly with manual state scope — includes all consensus-critical block-level processing
- **Block mode** (`--mode=Block`): Runs `BranchProcessor.Process` — adds state scope management, CommitTree, Reset, and TxHashCalculator on top of BlockOne
- **NewPayload mode** (`--mode=NewPayload`): Full `engine_newPayloadV4` path — JSON deserialization, `ExecutionPayloadV3`, `TryGetBlock()`, ECDSA sender recovery, `BranchProcessor.Process`
- **`--chunk N/M`**: Splits 910 test scenarios across M parallel runners for CI
- **GitHub Action** (`gas-benchmarks-bdn.yml`): Runs on master push and PR label, caches baselines, posts comparison comments
- Auto-discovers test scenarios from `tools/gas-benchmarks/eest_tests/`
- Reports **MGas/s** with 99% confidence intervals
- Verifies both **state root** and **block hash** match expected values from payload during warmup

### Architecture: what each mode measures

```
                                    NewPayload mode
                              ┌──────────────────────┐
                              │  JSON deserialization │
                              │  ExecutionPayloadV3   │
                              │  TryGetBlock (RLP)    │
                              │  ECDSA sender recovery│
                              └──────────┬───────────┘
                                         │
                                         ▼
                                  Block mode
                              ┌──────────────────────┐
                              │  BranchProcessor      │
                              │  ├─ BeginScope        │
                              │  ├─ CommitTree        │
                              │  ├─ Reset             │
                              │  └─ TxHashCalculator  │
                              └──────────┬───────────┘
                                         │
                                         ▼
                                BlockOne mode
                              ┌──────────────────────┐
                              │  BlockProcessor       │
                              │  .ProcessOne()        │
                              │  ├─ EIP-4788 beacon   │
                              │  ├─ EIP-2935 blockhash│
                              │  ├─ Tx execution      │
                              │  ├─ Bloom filters     │
                              │  ├─ Receipts root     │
                              │  ├─ EIP-4895 withdraw │
                              │  ├─ EIP-7685 requests │
                              │  ├─ State root calc   │
                              │  └─ Block hash calc   │
                              └──────────┬───────────┘
                                         │
                                         ▼
                                  EVM mode
                              ┌──────────────────────┐
                              │  TransactionProcessor │
                              │  .BuildUp()           │
                              │  ├─ EVM execution     │
                              │  └─ State changes     │
                              └──────────────────────┘
```

### What each mode is for

| Mode | Class | Use case | What it measures |
|------|-------|----------|-----------------|
| **EVM** | `GasPayloadBenchmarks` | Opcode optimization, EVM changes | Pure EVM + tx processing throughput |
| **BlockOne** | `GasBlockOneBenchmarks` | Block processing changes, state/trie work | `BlockProcessor.ProcessOne` with all consensus steps |
| **Block** | `GasBlockBenchmarks` | BranchProcessor overhead analysis | ProcessOne + scope management + tree commits |
| **NewPayload** | `GasNewPayloadBenchmarks` | End-to-end newPayload overhead | JSON deser + RLP decode + ECDSA recovery + block processing |

### Benchmark results

#### ether_transfer (4,761 txs per block — shows overhead clearly)

| Mode | Scenario | Mean | MGas/s | Allocated |
|------|----------|------|--------|-----------|
| **EVM** | block_ether_transfer_to_a | 11.99 ms | 8,338 | 17.88 MB |
| **EVM** | block_ether_transfer_to_b | 11.79 ms | 8,481 | 17.28 MB |
| **BlockOne** | block_ether_transfer_to_a | 29.67 ms | 3,370 | 34.14 MB |
| **BlockOne** | block_ether_transfer_to_b | 29.56 ms | 3,384 | 33.72 MB |
| **Block** | block_ether_transfer_to_a | 32.00 ms | 3,125 | 34.24 MB |
| **Block** | block_ether_transfer_to_b | 31.97 ms | 3,128 | 33.72 MB |
| **NewPayload** | block_ether_transfer_to_a | 332.3 ms | 301 | 50.1 MB |
| **NewPayload** | block_ether_transfer_to_b | 308.6 ms | 324 | 47.5 MB |

**Key insight**: For transaction-heavy blocks (4,761 txs), the overhead hierarchy is clear:
- EVM to BlockOne: ~2.5x slower (block-level consensus: state root, receipts root, bloom, EIP-4788/2935/4895/7685)
- BlockOne to Block: ~5% slower (BranchProcessor scope management overhead)
- Block to NewPayload: ~10x slower (JSON deserialization + ECDSA sender recovery for 4,761 txs dominates)

#### MULMOD (1 tx per block — compute-heavy, overhead is negligible)

| Mode | Scenario | Mean | MGas/s |
|------|----------|------|--------|
| **EVM** | mod_bits_63 | 400.3 ms | 249.79 |
| **EVM** | mod_bits_127 | 710.4 ms | 140.76 |
| **EVM** | mod_bits_255 | 835.8 ms | 119.65 |
| **BlockOne** | mod_bits_63 | 395.8 ms | 252.68 |
| **BlockOne** | mod_bits_127 | 738.3 ms | 135.44 |
| **BlockOne** | mod_bits_255 | 850.8 ms | 117.54 |
| **Block** | mod_bits_63 | 350.6 ms | 285.24 |
| **Block** | mod_bits_127 | 701.7 ms | 142.52 |
| **Block** | mod_bits_255 | 768.5 ms | 130.13 |
| **NewPayload** | mod_bits_63 | 375.8 ms | 266.12 |
| **NewPayload** | mod_bits_127 | 671.3 ms | 148.96 |
| **NewPayload** | mod_bits_255 | 788.7 ms | 126.79 |

**Key insight**: For 1-tx compute-heavy blocks, EVM execution dominates and all modes are within noise. The overhead of block-level processing, JSON deserialization, and sender recovery is negligible compared to the MULMOD computation.

#### NewPayload timing breakdown (mod_bits_127, 23 iterations)

```
  JSON parse:                0 ms  (0.0%)  avg 0.0 ms/iter
  Payload deserialize:       0 ms  (0.0%)  avg 0.0 ms/iter
  TryGetBlock:               0 ms  (0.0%)  avg 0.0 ms/iter
  Sender recovery:           0 ms  (0.0%)  avg 0.0 ms/iter
  Block processing:      15460 ms  (100.0%)  avg 672.2 ms/iter
```

For 1-tx blocks, all NewPayload-specific overhead rounds to 0ms. For high-tx-count blocks like ether_transfer, sender recovery and JSON deserialization become the dominant cost.

### Architecture diagram: Native vs BDN modes

#### Native gas-benchmarks (Nethermind node via HTTP)

```
┌──────────────┐         ┌──────────────────────────────────────────────────┐
│  Kute tool   │  HTTP   │  Nethermind Node                                │
│              │────────▸│                                                  │
│  payload.txt │         │  engine_newPayloadV4                            │
│              │         │    ├─ JSON-RPC deserialization                   │
│              │         │    ├─ SemaphoreSlim lock acquisition             │
│              │         │    ├─ GC.TryStartNoGCRegion(512MB)              │
│              │         │    ├─ ExecutionPayload to Block conversion       │
│              │         │    │    ├─ RLP transaction decoding              │
│              │         │    │    ├─ TxTrie.CalculateRoot                  │
│              │         │    │    └─ WithdrawalsTrie root                  │
│              │         │    ├─ Header hash validation                     │
│              │         │    ├─ Invalid chain tracker check                │
│              │         │    ├─ Parent header lookup (RocksDB read)        │
│              │         │    ├─ ShouldProcessBlock decision                │
│              │         │    │    └─ HasStateForBlock (RocksDB read)       │
│              │         │    ├─ ValidateSuggestedBlock                     │
│              │         │    ├─ BlockTree.SuggestBlock                     │
│              │         │    │    ├─ IsKnownBlock (RocksDB read)           │
│              │         │    │    ├─ blockStore.Insert (RocksDB write)     │
│              │         │    │    ├─ headerStore.Insert (RocksDB write)    │
│              │         │    │    └─ UpdateOrCreateLevel (RocksDB write)   │
│              │         │    ├─ Enqueue to BlockchainProcessor             │
│              │         │    │    └─ Channel + TaskCompletionSource        │
│              │         │    │                                             │
│              │         │    ├─ ┌─────────────────────────────────┐        │
│              │         │    │  │  BlockProcessor.ProcessOne()    │        │
│              │         │    │  │  (same work as BDN Block mode)  │        │
│              │         │    │  └─────────────────────────────────┘        │
│              │         │    │                                             │
│              │         │    ├─ GC.EndNoGCRegion()                         │
│              │         │    ├─ Scheduled Gen2 GC with compaction          │
│              │         │    ├─ SemaphoreSlim release                      │
│              │         │    └─ JSON response serialization                │
│              │◂────────│                                                  │
│              │         │  engine_forkchoiceUpdatedV3                      │
│              │────────▸│    ├─ JSON-RPC deserialization                   │
│              │         │    ├─ SemaphoreSlim lock acquisition             │
│              │         │    ├─ GetBlock (RocksDB read)                    │
│              │         │    ├─ TryGetBranch (chain traversal)             │
│              │         │    │    └─ Multiple FindParent (RocksDB reads)   │
│              │         │    ├─ UpdateMainChain (RocksDB writes)           │
│              │         │    ├─ MarkFinalized                              │
│              │         │    ├─ ForkChoiceUpdated (RocksDB writes)         │
│              │         │    ├─ SemaphoreSlim release                      │
│              │         │    └─ JSON response serialization                │
│              │◂────────│                                                  │
└──────────────┘         └──────────────────────────────────────────────────┘
                                        │
                              RocksDB ◂──┘ all state reads/writes
```

### Files

| File | Purpose |
|------|---------|
| `GasPayloadBenchmarks.cs` | EVM mode — injects txs via `TransactionProcessor.BuildUp` |
| `GasBlockOneBenchmarks.cs` | **NEW** — BlockOne mode — runs `BlockProcessor.ProcessOne` directly |
| `GasBlockBenchmarks.cs` | Block mode — runs `BranchProcessor.Process` |
| `GasNewPayloadBenchmarks.cs` | **NEW** — NewPayload mode — JSON deser + TryGetBlock + sender recovery + BranchProcessor |
| `BlockBenchmarkHelper.cs` | **NEW** — Shared setup code for block-level benchmarks |
| `PayloadLoader.cs` | Parses `engine_newPayloadV4` JSON; `LoadPayload()`, `LoadBlock()`, `ReadRawJson()`, `VerifyProcessedBlock()` |
| `GasBenchmarkColumnProvider.cs` | Custom BDN columns: MGas/s, CI-Lower, CI-Upper |
| `GasBenchmarkConfig.cs` | BDN config + `ChunkIndex`/`ChunkTotal` for CI splitting |
| `Program.cs` | Entry point with `--mode`, `--chunk`, `--diag`, `--inprocess` flag handling |
| `gas-benchmarks-bdn.yml` | GitHub Actions workflow for CI benchmark regression detection |
| `merge_bdn_results.py` | Merges BDN JSON results from chunked CI runners |
| `compare_bdn_results.py` | Compares master vs PR results, outputs Markdown table |

### Prerequisites

```bash
# Ensure Git LFS is installed (one-time), then initialize the submodule
git lfs install && git submodule update --init tools/gas-benchmarks
```

If the submodule was already cloned without LFS installed (genesis file shows as ~130 bytes instead of ~53MB):
```bash
git lfs install && cd tools/gas-benchmarks && git lfs pull
```

### Usage

```bash
# EVM mode — pure EVM throughput
dotnet run --project src/Nethermind/Nethermind.Evm.Benchmark -c Release -- --inprocess --mode=EVM --filter "*MULMOD*"

# BlockOne mode — BlockProcessor.ProcessOne directly
dotnet run --project src/Nethermind/Nethermind.Evm.Benchmark -c Release -- --inprocess --mode=BlockOne --filter "*MULMOD*"

# Block mode — BranchProcessor.Process
dotnet run --project src/Nethermind/Nethermind.Evm.Benchmark -c Release -- --inprocess --mode=Block --filter "*MULMOD*"

# NewPayload mode — full engine_newPayloadV4 path
dotnet run --project src/Nethermind/Nethermind.Evm.Benchmark -c Release -- --inprocess --mode=NewPayload --filter "*ether_transfer*"

# All gas benchmarks (all modes)
dotnet run --project src/Nethermind/Nethermind.Evm.Benchmark -c Release -- --inprocess --filter "*Gas*"

# Split across 5 runners (for CI)
dotnet run --project src/Nethermind/Nethermind.Evm.Benchmark -c Release -- --inprocess --mode=Block --chunk 2/5

# Diagnostic mode (quick debugging, no BDN harness)
dotnet run --project src/Nethermind/Nethermind.Evm.Benchmark -c Release -- --diag

# List all available scenarios
dotnet run --project src/Nethermind/Nethermind.Evm.Benchmark -c Release -- --list flat --filter "*Gas*"
```

### CI Workflow

The `gas-benchmarks-bdn.yml` workflow:
- **Triggers**: master push (paths: `src/Nethermind/**`), PR with `benchmark` label, `workflow_dispatch`
- **Runs**: 5 parallel self-hosted benchmark runners via `--chunk N/5`
- **On master**: Merges chunk results, caches as baseline (`gas-benchmark-bdn-{sha}`)
- **On PR**: Restores master baseline, compares with `compare_bdn_results.py`, posts sticky PR comment with regression/improvement table

### Correctness verification

All block-level modes (BlockOne, Block, NewPayload) verify during warmup that both **state root** and **block hash** match expected values from the payload file. This confirms all consensus-critical steps produce identical results to a real Nethermind node.

## Types of changes

#### What types of changes does your code introduce?

- [ ] Bugfix (a non-breaking change that fixes an issue)
- [x] New feature (a non-breaking change that adds functionality)
- [ ] Breaking change (a change that causes existing functionality not to work as expected)
- [ ] Optimization
- [ ] Refactoring
- [ ] Documentation update
- [x] Build-related changes
- [ ] Other: _Description_

## Testing

#### Requires testing

- [x] Yes
- [ ] No

#### If yes, did you write tests?

- [ ] Yes
- [x] No

#### Notes on testing

Benchmarks are self-verifying:
- Block-level modes verify state root and block hash match expected values for every scenario
- Tested with MULMOD, selfbalance, ether_transfer (4,761 txs per block) — all pass across all 4 modes
- EVM mode preserves existing behavior (no changes to `GasPayloadBenchmarks`)
- `--chunk N/M` verified: splitting across 5 chunks covers all 910 scenarios
- `--mode` + `--filter` combination verified (e.g. `--mode=Block --filter "*MULMOD*"`)

## Documentation

#### Requires documentation update

- [ ] Yes
- [x] No

#### Requires explanation in Release Notes

- [ ] Yes
- [x] No

## Remarks

- The gas-benchmarks genesis file (~53MB) is stored in Git LFS. Clear error messages guide users through setup if LFS or the submodule is missing.
- BDN config uses `MediumRun` with 1 launch and 10 iterations per scenario.
- Results JSON is exported to `BenchmarkDotNet.Artifacts/` for automated comparison scripts.
- All block-level modes use `ProcessingOptions.NoValidation | ProcessingOptions.ForceProcessing` to skip post-processing validation and allow re-processing the same block across iterations.
- State is reverted between iterations by disposing the WorldState scope and re-opening at the pre-block header.
- NewPayload mode includes a Stopwatch-based timing breakdown (printed in GlobalCleanup) showing how time is split between JSON parse, payload deserialize, TryGetBlock, sender recovery, and block processing.

## Update (2026-02-17)

Added NewPayloadMeasured mode (`--mode=NewPayloadMeasured`) and reran a single snapshot across all modes with:
`--inprocess --warmupCount 10 --iterationCount 10 --launchCount 1`

Scenario filters:
- `a_to_a`: `*balance_0-case_id_a_to_a*`
- `selfbalance`: `*contract_balance_0*`

| Scenario | Mode | Mean | MGas/s | Allocated |
|------|------|------:|------:|------:|
| a_to_a | EVM | 11.20 ms | 8931.08 | 17.88 MB |
| a_to_a | BlockOne | 30.05 ms | 3327.42 | 34.02 MB |
| a_to_a | Block | 36.42 ms | 2745.38 | 53.11 MB |
| a_to_a | NewPayload | 105.5 ms | 948.06 | 70.18 MB |
| a_to_a | NewPayloadMeasured | 98.67 ms | 1013.44 | 70.07 MB |
| selfbalance | EVM | 284.0 ms | 352.09 | 9.33 MB |
| selfbalance | BlockOne | 272.9 ms | 366.40 | 9.49 MB |
| selfbalance | Block | 274.8 ms | 363.96 | 9.50 MB |
| selfbalance | NewPayload | 268.3 ms | 372.76 | 9.52 MB |
| selfbalance | NewPayloadMeasured | 254.2 ms | 393.42 | 9.51 MB |

Artifacts:
- Summary CSV: `BenchmarkDotNet.Artifacts/results/mode-comparison-final-20260217-010906.csv`
- Breakdown index: `BenchmarkDotNet.Artifacts/results/newpayload-breakdown-files-final-20260217-010906.csv`
