Reth v2.0.0 trie investigation and first port
==========================================

The target workload is Nethermind's flat-state EXPB Fusaka benchmark, triggered by
the `performance is good` PR label. This prototype ports Reth's two-nibble root
hashing partition into the existing Patricia trie. It does **not** implement the
complete persistent sparse-trie cache or concurrent execution/update pipeline,
and there is no measured Fusaka improvement for this patch yet.

Reth's [announcement](https://www.paradigm.xyz/writing/releasing-reth-2-0) describes
1–2 ms of final state-root work after updates have already been processed alongside
execution. It is not a measurement of all trie work for the block.

The v2.0.0 sources show four relevant mechanisms:

- [SparseTrieCacheTask](https://github.com/paradigmxyz/reth/blob/v2.0.0/crates/engine/tree/src/tree/payload_processor/sparse_trie.rs)
  consumes execution updates, schedules missing proofs, applies updates, and
  opportunistically computes subtries before the final root. It records total
  task time and final root/update time separately.
- [PreservedSparseTrie](https://github.com/paradigmxyz/reth/blob/v2.0.0/crates/engine/tree/src/tree/payload_processor/preserved_sparse_trie.rs)
  retains a trie anchored to the previous state root. A parent-root mismatch
  clears its contents; invalid/cancelled payload handling also clears state.
- [SparseStateTrie](https://github.com/paradigmxyz/reth/blob/v2.0.0/crates/trie/sparse/src/state.rs)
  retains hot account and storage paths with bounded LFU tracking and prunes
  other paths to hash references.
- [ParallelSparseTrie](https://github.com/paradigmxyz/reth/blob/v2.0.0/crates/trie/sparse/src/parallel.rs)
  and the [arena implementation](https://github.com/paradigmxyz/reth/blob/v2.0.0/crates/trie/sparse/src/arena/mod.rs)
  partition at two nibbles, compute changed lower subtries in parallel, then
  finish the upper trie. The arena also replaces pointer-heavy node allocation
  with indexed storage and reusable traversal/encoding buffers.

Nethermind already skips nodes with a cached hash and loads trie paths lazily.
Flat state also reuses nodes through snapshots, transient resources, and
`TrieNodeCache`; it does not start every block with an entirely cold trie.
Its existing root encoder parallelizes over at most 16 immediate root children.
The prototype gathers up to 256 materialized, unhashed lower subtries without
resolving unchanged siblings, hashes those in one parallel loop, then encodes the
upper trie serially. Small frontiers, extension roots, and serial callers retain
the existing path. This preserves the current RLP, Keccak, persistence, and
copy-on-write implementations.

`TrieRootBenchmark` compares the previous root-child schedule directly with the
new `UpdateRootHash` path. Both start from the same persisted 131,072-key trie.
Loading and applying updates happen in iteration setup, outside the measurement.
Values are 80 bytes; keys are deterministic random 32-byte values. The clustered
case restricts changes to four of the sixteen root children. It is a scheduling
stress case, not a model of uniformly hashed mainnet account changes.

Initial local results: Windows x64, .NET 10.0.9, AVX2, server GC,
BenchmarkDotNet 0.15.8 in-process, five warmups and twenty measured iterations.
These short single-invocation measurements have substantial scheduling noise;
uniform-workload confidence intervals overlap. The host does not support
AVX-512VL, so interactions with Nethermind's batched hashing remain unmeasured.

| Changed keys | Distribution | Existing mean | Prototype mean |
|---:|---|---:|---:|
| 64 | Uniform | 181.38 us | 186.87 us |
| 64 | Clustered | 190.86 us | 114.16 us |
| 1,024 | Uniform | 449.27 us | 429.07 us |
| 1,024 | Clustered | 553.54 us | 311.19 us |
| 8,192 | Uniform | 1,821.31 us | 1,585.30 us |
| 8,192 | Clustered | 2,394.10 us | 987.01 us |

Reproduce after building the benchmark runner:

```powershell
dotnet artifacts/bin/Nethermind.Benchmark.Runner/release/Nethermind.Benchmark.Runner.dll --filter '*TrieRootBenchmark*' --inProcess --warmupCount 5 --iterationCount 20 --launchCount 1 --artifacts artifacts/reth-root-bench-inprocess
```

The in-process toolchain avoids BenchmarkDotNet discovering duplicate project
names in the local `.review-worktrees` directory. Results are in
`artifacts/reth-root-bench-inprocess/results/Nethermind.Benchmarks.Store.TrieRootBenchmark-report-full-compressed.json`.

Validation so far: the complete trie suite passed 602 tests and skipped 12,
including six new cases comparing against a fresh serial trie through deletion,
reinsertion, commit/reload, cached-root reuse, short embedded nodes, and shared
prefixes. Five skips require AVX-512VL; the others are pre-existing exclusions.
The selected flat-state scope, historical-root, and overridable-scope suites also
passed all 54 tests.

The [master Fusaka job inspected](https://github.com/NethermindEth/nethermind/actions/runs/35705708272/job/106675539225)
reports 26.85 ms mean block processing and 30.99 ms mean request time. It does not
expose a root-only timing in its log, so it cannot independently confirm the
reported 6–7 ms. Cleanup completed; the log contains SSE connection-refused
messages during startup and no `Nethermind is shut down` marker. This run is
context, not an A/B result for the prototype.

The next end-to-end check is matched unprofiled flat/Fusaka EXPB runs on the same
runner and payload range, with repeat runs and both processing and request
timings. Root-only attribution should distinguish `BlockProcessor.ComputeStateRoot`
from `CommitStateAndStorageRoots`; the latter includes storage-root work. Slow
block diagnostics expose those timings separately. A separate sampling profile
can establish whether root hashing, account updates, storage roots, or trie
loading dominates before expanding this port.

A full cache/pipeline port would need a scope-owned mutable working trie,
ordered execution updates, bounded retention of account and storage paths,
parent-root anchoring, and invalid-payload/reorg/cancellation cleanup. Mutating
nodes shared with flat snapshots or trie warmers would violate existing ownership
rules. The current prototype deliberately leaves that architecture for a separate
change whose benefit can be measured independently.
