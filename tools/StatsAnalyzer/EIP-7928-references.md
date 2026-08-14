# EIP-7928 — Block Access Lists — Cross-Reference

Audit map between [EIP-7928] (Block-Level Access Lists), the canonical
[execution-specs] reference implementation, and Nethermind's C# code. Used
by the StatsAnalyzer plugin to justify where it intercepts and what
correctness boundaries apply.

This doc is **not** an EIP-7928 tutorial. For background, read EIP-7928
proper.

[EIP-7928]: https://eips.ethereum.org/EIPS/eip-7928
[execution-specs]: https://github.com/ethereum/execution-specs

## 1. Canonical spec

Reference implementation lives in the execution-specs repo:

- `src/ethereum/forks/amsterdam/block_access_lists.py`
- `src/ethereum/forks/amsterdam/state_tracker.py`
- `src/ethereum/forks/amsterdam/vm/gas.py`
- `src/ethereum/forks/amsterdam/fork.py`

Locally vendored at `/Users/sid/dev/ethereum/specs/execution-specs/`, pinned
to commit `c8117f22bf61da4f163f349d97a9833389dd62b9` at the time of writing
(2026-05-18).

The module docstring (`block_access_lists.py:1-13`) is the canonical
statement of what BALs enable:

> Block access lists (BALs), originally defined in EIP-7928, record all
> accounts and storage locations accessed during block execution along with
> their post-execution values.
>
> BALs enable parallel disk reads, parallel transaction validation,
> parallel state root computation, and applying state updates without
> executing bytecode.

Important: parallelism is described as something BALs *enable*, not
something they *mandate*. The spec is silent on execution scheduling —
client implementations are free to run sequentially or in parallel. The
StatsAnalyzer stopgap (§5 below) leans on this: skipping recording on
parallel-mode blocks does not violate any spec requirement.

## 2. EIP element ↔ Nethermind implementation

| EIP-7928 element | Spec ref (execution-specs path:line) | Nethermind impl |
|---|---|---|
| `BlockAccessList` type alias | `forks/amsterdam/block_access_lists.py:247` | `src/Nethermind/Nethermind.Core/BlockAccessLists/BlockAccessList.cs` |
| `AccountChanges` dataclass | `forks/amsterdam/block_access_lists.py:199-245` | `src/Nethermind/Nethermind.Core/BlockAccessLists/AccountChanges.cs` |
| Per-tx update (`update_builder_from_tx`) | `forks/amsterdam/block_access_lists.py:647-702` | `src/Nethermind/Nethermind.State/TracedAccessWorldState.cs` |
| RLP serialization | `forks/amsterdam/block_access_lists.py:~734` | `src/Nethermind/Nethermind.Serialization.Rlp/Eip7928/{BlockAccessListDecoder,AccountChangesDecoder}.cs` |
| Block-gas-limit validation | `forks/amsterdam/block_access_lists.py:737-770` | `src/Nethermind/Nethermind.Consensus/Processing/BlockAccessListManager.cs:305-400` |
| Header `block_access_list_hash` | `forks/amsterdam/fork.py:333-356` | `src/Nethermind/Nethermind.Core/BlockHeader.cs:78` (`BlockAccessListHash`) |
| Item-gas constant | `forks/amsterdam/vm/gas.py:103` (`BLOCK_ACCESS_LIST_ITEM = Uint(2000)`) | `Nethermind.Core/BlockAccessLists/` (see `Eip7928Constants`) |
| Fork activation | `forks/amsterdam/__init__.py` | `src/Nethermind/Nethermind.Specs/Forks/25_Amsterdam.cs:17` (`IsEip7928Enabled = true`) |

## 3. Parallel execution (implementation-only)

Nethermind exploits the BAL's pre-declared read/write set to execute
transactions across worker threads. This is a Nethermind optimization, not
an EIP-7928 requirement.

Flag chain:

1. Config knob — `IBlocksConfig.ParallelExecution`
   (`src/Nethermind/Nethermind.Config/IBlocksConfig.cs:64-67`), default
   `true`.
2. Per-block decision — `BlockAccessListManager.ParallelExecutionEnabled`
   (`src/Nethermind/Nethermind.Consensus/Processing/BlockAccessListManager.cs:91`):
   ```
   ParallelExecutionEnabled =
       Enabled
    && blocksConfig.ParallelExecution
    && !_isBuilding
    && suggestedBlock.BlockAccessList is not null;
   ```
   Where `Enabled = spec.BlockLevelAccessListsEnabled && !suggestedBlock.IsGenesis` (line 86)
   and `_isBuilding = options.ContainsFlag(ProcessingOptions.ProducingBlock)` (line 87).
3. Execution path — `ParallelTxProcessorWithWorldStateManager`
   (`src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.ParallelBlockValidationTransactionsExecutor.cs`).
   Worker threads each own a `TracedAccessWorldState` and a per-worker BAL;
   `MergeAndReturnBal` (`BlockAccessListManager.cs:171, 203`) combines them
   at end-of-block.

Build (local block production) is *always* sequential — the `!_isBuilding`
clause forces it.

## 4. StatsAnalyzer interception

The plugin's existing Pattern + Call analyzers share mutable state across
transactions (a shared `Tracer` field, the shared `_ngram` rolling buffer
in `PatternStatsAnalyzer`, shared `_counts` dict in `CallStatsAnalyzer`,
shared `CmSketch._sketch` cells). Under parallel BAL execution the per-tx
hot-path methods (`StartOperation`, `ReportAction`) are invoked from
multiple worker threads concurrently, racing on that state and — in the
n-gram case — silently producing invalid statistics (instructions from
different parallel txs interleaved into one n-gram).

The stopgap intercepts in `StatsAnalyzerFileTracer<,>.StartNewBlockTrace`:

```csharp
_skipThisBlock = _blocksConfig.ParallelExecution
              && block.BlockAccessList is not null;
Tracer.SetSkip(_skipThisBlock);
```

This deliberately approximates the authoritative flag from §3 step 2. The
two omitted clauses (`Enabled`, `!_isBuilding`) are subtractive: when
false, parallel exec is *off* even if our approximation says on. The
approximation therefore never under-skips (no correctness leak) and only
over-skips in narrow, benign cases (pre-Amsterdam misconfig, or local
block production with a pre-populated BAL — both yield "we lose a few
blocks of stats", no race).

When `_skipThisBlock` is true:

- `PatternStatsAnalyzerTxTracer.StartOperation` short-circuits.
- `PatternStatsAnalyzerTxTracer.AddTxEndMarker` short-circuits.
- `CallStatsAnalyzerTxTracer.ReportAction` short-circuits.
- `StatsAnalyzerFileTracer.EndBlockTrace` skips the file write.
- `_initialBlock` / `_currentBlock` do not advance, so the recorded range
  reflects only blocks actually written.

The stopgap is a placeholder. The proper fix — per-worker accumulators
plus a merge at end-of-block — is left as follow-up work.

## 5. Spec-drift checklist

If you re-vendor execution-specs to a newer commit, re-verify these
positions are still correct:

- [ ] `BlockAccessList` type alias at `forks/amsterdam/block_access_lists.py:~247`
- [ ] `AccountChanges` dataclass at `forks/amsterdam/block_access_lists.py:199-245`
- [ ] `update_builder_from_tx` at `forks/amsterdam/block_access_lists.py:647-702`
- [ ] `validate_block_access_list_gas_limit` at `forks/amsterdam/block_access_lists.py:737-770`
- [ ] `BLOCK_ACCESS_LIST_ITEM = Uint(2000)` at `forks/amsterdam/vm/gas.py:103`
- [ ] Amsterdam fork activation in `forks/amsterdam/__init__.py`

And on the Nethermind side:

- [ ] `BlockAccessListManager.ParallelExecutionEnabled` definition
  (currently `Nethermind.Consensus/Processing/BlockAccessListManager.cs:91`)
- [ ] `IBlocksConfig.ParallelExecution` default value (currently `true` at
  `Nethermind.Config/IBlocksConfig.cs:64-67`)
- [ ] `Block.BlockAccessList` settable property (currently
  `Nethermind.Core/Block.cs:129`)
