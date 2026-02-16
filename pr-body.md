## Changes

Add BenchmarkDotNet gas benchmarks with two modes (**EVM** and **Block**) that replay [gas-benchmarks](https://github.com/NethermindEth/gas-benchmarks) `engine_newPayloadV4` payload files, plus a GitHub Actions workflow for automated CI regression detection.

- **EVM mode** (`--mode=EVM`): Injects transactions directly into `TransactionProcessor.BuildUp` — measures pure EVM + transaction processing throughput
- **Block mode** (`--mode=Block`): Runs the full `BlockProcessor.ProcessOne` pipeline — includes all consensus-critical block-level processing
- **`--chunk N/M`**: Splits 910 test scenarios across M parallel runners for CI
- **GitHub Action** (`gas-benchmarks-bdn.yml`): Runs on master push and PR label, caches baselines, posts comparison comments
- Auto-discovers test scenarios from `tools/gas-benchmarks/eest_tests/`
- Reports **MGas/s** with 99% confidence intervals
- Verifies both **state root** and **block hash** match expected values from payload during warmup

### How the three benchmark approaches compare

#### EVM Mode (BDN in-process)

```
┌─────────────────────────────────────────────────────────────┐
│  BenchmarkDotNet Process                                    │
│                                                             │
│  payload.txt ──parse──▸ BlockHeader + Transaction[]         │
│                              │                              │
│                              ▼                              │
│                  TransactionProcessor.BuildUp()             │
│                    ├─ EVM execution                         │
│                    └─ State changes (not committed)         │
│                              │                              │
│                              ▼                              │
│                       state.Reset()                         │
│                    (revert for next iteration)              │
│                                                             │
│  In-memory TrieStore ◂── all state reads/writes            │
└─────────────────────────────────────────────────────────────┘
```

Measures: **Pure EVM opcode throughput**. No block-level overhead, no state root calculation, no trie commits.

#### Block Mode (BDN in-process)

```
┌─────────────────────────────────────────────────────────────┐
│  BenchmarkDotNet Process                                    │
│                                                             │
│  payload.txt ──parse──▸ Block (header, txs, withdrawals)   │
│                              │                              │
│                              ▼                              │
│               BlockProcessor.ProcessOne()                   │
│                 ├─ BeaconBlockRootHandler    (EIP-4788)     │
│                 ├─ BlockhashStore            (EIP-2935)     │
│                 ├─ State commit                             │
│                 ├─ Transaction execution (all txs)          │
│                 ├─ State commit                             │
│                 ├─ Bloom filters                            │
│                 ├─ Blob gas validation       (EIP-4844)     │
│                 ├─ Receipts root calculation                │
│                 ├─ Withdrawals processing    (EIP-4895)     │
│                 ├─ State commit                             │
│                 ├─ Execution requests        (EIP-7685)     │
│                 ├─ State commit (with roots)                │
│                 ├─ State root recalculation                 │
│                 └─ Block hash calculation                   │
│                              │                              │
│                              ▼                              │
│                  Verify stateRoot + blockHash               │
│                  match expected from payload                │
│                              │                              │
│                              ▼                              │
│                  Dispose scope ──▸ BeginScope               │
│                    (revert for next iteration)              │
│                                                             │
│  In-memory TrieStore ◂── all state reads/writes            │
└─────────────────────────────────────────────────────────────┘
```

Measures: **Full block processing throughput** minus I/O and transport. All consensus-critical steps are included. State root and block hash are verified against the payload to confirm correctness.

#### Native gas-benchmarks (Nethermind node via HTTP)

```
┌──────────────┐         ┌──────────────────────────────────────────────────┐
│  Kute tool   │  HTTP   │  Nethermind Node                                │
│              │────────▸│                                                  │
│  payload.txt │         │  engine_newPayloadV4                            │
│              │         │    ├─ JSON-RPC deserialization                   │
│              │         │    ├─ SemaphoreSlim lock acquisition             │
│              │         │    ├─ GC.TryStartNoGCRegion(512MB)              │
│              │         │    ├─ ExecutionPayload → Block conversion        │
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

Measures: **End-to-end block import throughput** including HTTP transport, JSON-RPC, block validation, RocksDB persistence, GC management, forkchoiceUpdated, and async queue coordination.

### What each mode is for

| Mode | Use case | What it isolates |
|------|----------|------------------|
| **EVM** | Opcode optimization, EVM changes | Pure EVM + tx processing throughput |
| **Block** | Block processing optimization, state/trie changes | Full consensus pipeline minus I/O |
| **Native** | End-to-end regression testing | Real-world block import including all infrastructure |

### Files

| File | Purpose |
|------|---------|
| `GasPayloadBenchmarks.cs` | EVM mode benchmark — injects txs via `TransactionProcessor.BuildUp` |
| `GasBlockBenchmarks.cs` | **NEW** — Block mode benchmark — runs `BlockProcessor.ProcessOne` with state root + block hash verification |
| `PayloadLoader.cs` | Parses `engine_newPayloadV4` JSON; `LoadPayload()` for EVM mode, `LoadBlock()` for Block mode |
| `GasBenchmarkColumnProvider.cs` | Custom BDN columns: MGas/s, CI-Lower, CI-Upper |
| `GasBenchmarkConfig.cs` | BDN config + `ChunkIndex`/`ChunkTotal` for CI splitting |
| `Program.cs` | Entry point with `--mode`, `--chunk`, `--diag`, `--inprocess` flag handling |
| `gas-benchmarks-bdn.yml` | **NEW** — GitHub Actions workflow for CI benchmark regression detection |
| `merge_bdn_results.py` | **NEW** — Merges BDN JSON results from chunked CI runners |
| `compare_bdn_results.py` | **NEW** — Compares master vs PR results, outputs Markdown table |

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

# Block mode — full block processing
dotnet run --project src/Nethermind/Nethermind.Evm.Benchmark -c Release -- --inprocess --mode=Block --filter "*MULMOD*"

# All gas benchmarks (both modes)
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

### Example output

```
| Method       | Scenario             | Mean     | StdDev   | MGas/s | CI-Lower | CI-Upper |
|------------- |--------------------- |---------:|---------:|-------:|---------:|---------:|
| ProcessBlock | mod_a(...)_127] [42] | 683.9 ms | 17.09 ms | 146.22 |   150.08 |   142.56 |
| ProcessBlock | mod_a(...)_255] [42] | 803.5 ms | 16.47 ms | 124.46 |   127.14 |   121.89 |
```

- **MGas/s**: `100M gas / mean_seconds / 1M` — higher is better
- **CI-Lower / CI-Upper**: 99% confidence interval bounds for MGas/s

### Correctness verification

Block mode verifies during warmup that both **state root** and **block hash** match expected values from the payload file. This confirms all consensus-critical steps (beacon root storage, blockhash store, transaction execution, bloom filters, receipts root, withdrawals, execution requests, state root recalculation) produce identical results to a real Nethermind node.

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
- Block mode verifies state root and block hash match expected values for every scenario
- Tested with MULMOD, selfbalance, ether_transfer (4,761 txs per block) — all pass
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
- Block mode uses `ProcessingOptions.NoValidation | ProcessingOptions.ForceProcessing` to skip post-processing validation and allow re-processing the same block across iterations.
- State is reverted between iterations by disposing the WorldState scope and re-opening at the pre-block header.
