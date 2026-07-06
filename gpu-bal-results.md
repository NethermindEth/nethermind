# BAL-Driven GPU State Root - Results

Status: results document for the `gpu-bal` branch shadow validator. Uncommitted by design.
Machine: AMD Ryzen 9 9950X (16 physical / 32 logical cores), Windows 11 (10.0.26220),
.NET 10.0.8, with an NVIDIA RTX PRO 6000 (CUDA) discrete card and an AMD gfx1036 iGPU
(OpenCL) both visible to ILGPU.

## (a) What was built

An EIP-7928 Block Access List carries every post-block state change, so the whole
post-block state delta is derivable at block arrival without executing. That enables a
validation lane independent of execution, implemented here as a SHADOW (computes, compares,
counts, logs; never affects which blocks are accepted).

Precision on the claim: the BAL alone yields only the DELTA. Computing the root also
requires the parent trie - the intermediate nodes along modified paths and the sibling
refs on their edges, which the BAL does not carry (no merkle proofs in EIP-7928). Lane B
reads those from local state through the read-only store at the parent root. The
independence is from EXECUTION, not from STATE: any synced full node can run Lane B at
block arrival, but a stateless verifier would additionally need a witness/multiproof of
the touched paths.

- Lane A: normal execution + exec-vs-BAL validation (unchanged production path).
- Lane B (shadow): `BalPostStateDelta.Reduce(bal)` reduces the BAL to final per-field/slot
  post-values (last-index-wins); `BalStateRootCalculator.ComputeRoot(parent, delta)`
  rebuilds the post-block state root three-pass (pre-state reads, storage roots + account
  composition, state writes) through a read-only, cloning trie store wrapped in
  `BeginScope(parent)`, never committing.
- `BalStateRootShadow` runs Lane B on a background `Task.Run` (no token) started before
  `ProcessOne`, compared via value-captured non-blocking continuations after; bounded
  in-flight (cap 4 + skip counter), atomic self-disable after consecutive errors, one
  startup capability Info line. Five metrics in `Nethermind.Blockchain/Metrics.cs`.
- Batch hashing backends behind `IKeccakBatchHasher`: `PerMessageKeccakBatchHasher`
  (baseline; already AVX-512-horizontal per message on capable hardware),
  `ParallelKeccakBatchHasher` (multi-core, per-message work-stealing),
  `MultiBufferKeccakBatchHasher` (vertical Vector512 8-way kernel - experiment, not selected
  for production), `GpuKeccakBatchHasher` (ILGPU, in leaf `Nethermind.Crypto.Gpu`), and
  `ThresholdKeccakBatchHasher` routing large batches to GPU / everything else to the CPU
  backend. `BatchedTrieCommitter.UpdateRootHashBatched` is the wave merkleizer that feeds
  wide per-level batches into any backend.

## (b) Benchmark table (GpuKeccakBatchBenchmarks)

BenchmarkDotNet in-process, ShortRun (3 warmup / 5 iterations), end-to-end dispatch
including host<->device transfers for GPU rows. Backend list is auto-discovered per host.
Batch profiles: Fixed100 (every message 100 bytes) and TrieMix (repeating 70/110/532-byte
leaf/branch/large-node shapes). Full mean-time matrix from the benchmark run
(commit 9bc1c94e49) on the machine above; ratios are vs the single-core per-message
baseline (higher = faster).

Profile: Fixed100

| Backend                                    | N=4096          | N=16384         | N=65536          | N=262144         |
|--------------------------------------------|-----------------|-----------------|------------------|------------------|
| PerMessage (single core, AVX-512 horizontal) | 1057 us (1.00x) | 4158 us (1.00x) | 16469 us (1.00x) | 65770 us (1.00x) |
| ParallelWorkStealing (multi-core)          | 141 us (7.5x)   | 456 us (9.1x)   | 1963 us (8.4x)   | 8528 us (7.7x)   |
| MultiBuffer (vertical 8-way, not selected) | 778 us (1.36x)  | 3111 us (1.34x) | 12425 us (1.33x) | 43762 us (1.50x) |
| GPU CUDA (RTX PRO 6000)                    | 320 us (3.3x)   | 488 us (8.5x)   | 1144 us (14.4x)  | 4835 us (13.6x)  |
| GPU OpenCL iGPU (gfx1036)                  | 888 us (1.19x)  | 1852 us (2.24x) | 6873 us (2.40x)  | 26960 us (2.44x) |

Profile: TrieMix (70/110/532)

| Backend                                    | N=4096          | N=16384         | N=65536          | N=262144          |
|--------------------------------------------|-----------------|-----------------|------------------|-------------------|
| PerMessage (single core, AVX-512 horizontal) | 2044 us (1.00x) | 8220 us (1.00x) | 33544 us (1.00x) | 131027 us (1.00x) |
| ParallelWorkStealing (multi-core)          | 182 us (11.2x)  | 702 us (11.7x)  | 2868 us (11.7x)  | 14634 us (9.0x)   |
| MultiBuffer (vertical 8-way, not selected) | 1322 us (1.55x) | 6126 us (1.34x) | 21712 us (1.55x) | 100257 us (1.31x) |
| GPU CUDA (RTX PRO 6000)                    | 834 us (2.5x)   | 1151 us (7.1x)  | 2594 us (12.9x)  | 10419 us (12.6x)  |
| GPU OpenCL iGPU (gfx1036)                  | 878 us (2.3x)   | 2488 us (3.3x)  | 10858 us (3.1x)  | 39912 us (3.3x)   |

Notes:
- The multi-core work-stealing backend is the leader through 16384 messages, up to 11.7x
  over the per-message baseline.
- CUDA crosses over the multi-core backend at ~65536 messages and stays ahead at 262144.
- The OpenCL iGPU never beats the multi-core backend within the measured range and is ~3x
  slower at 262144.
- Vertical multi-buffer kernel: NOT SELECTED for production. It is modestly faster than the
  single-core per-message baseline (1.3-1.55x - lane transposition and snapshot bookkeeping
  eat most of the theoretical 8x), but on the same single core it is 4-9x slower than the
  multi-core work-stealing backend and far behind the GPU at scale, so it loses to every
  alternative that matters. Retained only as a differential-tested experiment. (The
  0.6-0.8x figure that appeared in an earlier note was a TIME ratio mislabeled as a speed
  ratio; the table above is the correct framing.)
- Work-stealing partition: per-message work-stealing beat static contiguous slices by -38%
  on the non-uniform trie mix at N=1024 (57.96us vs 93.29us), ties on uniform.

Composition experiment (separate later run, same ShortRun methodology, its own baselines;
benchmark-local ParallelMultiBufferKeccakBatchHasher = vertical 8-way kernel under
multi-core work-stealing; measured alongside re-runs of the backends it competes with -
those re-runs agreed with the table above within run-to-run noise):

| Backend (that run)          | Fixed100 4k | 16k | 64k | 256k | TrieMix 4k | 16k | 64k | 256k |
|-----------------------------|-------------|-----|-----|------|------------|-----|-----|------|
| ParallelWorkStealing        | 8.2x | 8.8x | 8.7x | 7.5x | 11.2x | 11.5x | 11.5x | 8.9x |
| ParallelMultiBuffer (local) | 10.4x | 9.9x | 10.0x | 10.1x | 10.3x | 12.2x | 11.5x | 11.6x |
| ParallelMultiBuffer DOP-4   | 4.2x | 4.2x | 4.1x | 4.6x | 4.7x | 5.0x | 4.5x | 4.5x |

Verdict: the composition does not multiply the two speedups (~15-35% ahead on uniform
lengths, a wash on the trie mix - grouping remainders, snapshots, and gather copies erode
the vertical edge exactly where lengths vary); not selected.

## (c) Crossover analysis and GpuMinBatch decision

The GPU pays a fixed host-device transfer + launch cost per dispatch that only amortizes on
very large batches. Below the crossover the multi-core CPU backend wins once that transfer
is counted. Measured CUDA-vs-multi-core crossover is ~65536 messages, so the `GpuMinBatch`
default was raised 4096 -> 65536. `ThresholdKeccakBatchHasher` routes batches >= GpuMinBatch
to the GPU and everything below (and any GPU fault thereafter) to the multi-core CPU
backend. Consequence: on pyspec- and mainnet-sized blocks, per-level trie batches are far
below 65536, so with UseGpu enabled the GPU stays dormant and the CPU backend does the work
- the GPU only engages on pathologically large single levels. This is the intended
production shape (GPU is an opportunistic accelerator, not the default path).

## System impact under contention (background-lane objective)

Isolated wall-clock throughput (section b) is the wrong objective for a lane that runs in the
BACKGROUND, concurrent with block execution: the cost that matters is CPU-seconds STOLEN from
Lane A, not how fast the batch finishes in isolation. `ContentionProbe` (commit d82a70ba98,
`src/Nethermind/Nethermind.Benchmark/Core/ContentionProbe.cs`, invoked via the benchmark runner
`--contention-probe`) measures this outside BenchmarkDotNet: process CPU-seconds per batch and
mutual interference with a CPU saturator. Numbers below are the MOST RECENT probe pass on this
box (Ryzen 9 9950X + RTX PRO 6000 + gfx1036), N=65536 TrieMix, 200 dispatches, idle machine;
coarse (one clean pass, not statistically rigorous) by design. An earlier pass measured the
same backends within ~10-25% of these values; the vertical multi-buffer row is from that
earlier pass (marked *) as the recent pass did not include it.

All backends side by side. `wall ms` = isolated latency per batch; `cpu ms` = process CPU-time
consumed per batch; `core-equivalents` = cpu/wall (how many cores the backend keeps busy for the
batch); `interference` = CPU-saturator throughput while the backend hashes continuously, relative
to the idle baseline (1.00x = no impact, lower = execution robbed):

| Backend                     | wall ms | cpu ms | core-equivalents | interference | what it costs vs delivers                        |
|-----------------------------|---------|--------|------------------|--------------|--------------------------------------------------|
| PerMessage                  | 33.07   | 32.42  | 0.98             | 0.99x        | slowest wall, but only one core                  |
| ParallelWorkStealing        | 2.93    | 46.64  | 15.93            | 0.95x        | fastest CPU wall, but ~16 cores busy             |
| MultiBuffer*                | 25.42   | 24.77  | 0.97             | -            | one core, slower than PerMessage (not selected)  |
| ParallelMultiBuffer (local) | 3.68    | 36.56  | 9.94             | 0.89x        | work-stealing-tier wall at ~10 cores; worst interference (gather traffic) |
| ParallelMultiBuffer DOP-4   | 8.13    | 31.95  | 3.93             | 0.97x        | ~4.5x on a 4-core budget                         |
| Gpu OpenCL iGPU             | 10.95   | 3.75   | 0.34             | 0.99x        | mid wall at ~0.34 cores; near-zero system impact |
| Gpu CUDA                    | 2.66    | 3.20   | 1.20             | 1.00x        | fastest AND ~1 core (sync busy-wait); zero saturator impact |

Composition experiment (measured, not selected): composing the vertical 8-way kernel with
multi-core work-stealing (benchmark-local ParallelMultiBufferKeccakBatchHasher) does NOT
multiply the two speedups - it lands at ~10-12x, the same tier as the multi-core
work-stealing backend (both draw from the same core budget; the per-message path is
already AVX-512-vectorized). See its rows in the table above: work-stealing-tier wall at
~10 core-equivalents but the worst co-running interference (per-run gather copies contend
for memory bandwidth); at DOP-4 it delivers ~4.5x on ~4 cores - respectable on a small
core budget, not a substitute for the full backend. The CUDA crossover is unchanged; no
case to select it.

The three cheap-latency options cost cores very differently: the work-stealing backend
hits 2.93 ms wall by consuming ~16 cores; CUDA hits 2.66 ms wall at ~1 core (a sync
busy-wait) with no measurable saturator impact; the iGPU sits at 10.95 ms wall but only
~0.34 cores (its dispatch thread sleeps while the device works). PerMessage is the
slowest at 33.07 ms wall but also single-core.

Latency was contention-insensitive (0.98-1.21x contended-vs-idle) because the metric that moves
under load is CPU-seconds, not wall time.

Interpretation (factual, no recommendation): for a background lane the relevant axis is
CPU-seconds stolen from Lane A, not isolated wall time, and on that axis the ranking differs from
section (b). The work-stealing backend wins the isolated wall-clock race above the crossover but
keeps ~15 cores busy per batch. CUDA delivers the same wall time at ~1 core with no measurable
saturator impact. The iGPU is the most CPU-frugal (~0.34 cores, essentially zero interference) even
though it loses every isolated wall-clock race. PerMessage matches the iGPU on cores (single-core)
but is the slowest in wall time. The vertical multi-buffer kernel is single-core like PerMessage but
slower, and was not selected in section (b).

## Device-resident MPT flow: measure-first evaluation (NO-GO for now)

Proposal: move per-level encode+hash+splice fully onto the GPU (chained kernels, no
per-level round trips) returning the complete changed-node set as (keccak, RLP) pairs -
exactly what Commit persists; CPU keeps trie restructuring + DB I/O. Would apply to the
STANDARD commit path too (the machinery is BAL-independent once a dirty set exists).

Measured (MptRootProbe, --mpt-root-probe, synthetic skewed shapes, MemDb, median of 9):

Measured over the FULL scope (storage tries + state trie; corrected after review found the
first pass measured storage tries only):

| Metric | 100 acct | 400 acct | 1600 acct |
|---|---|---|---|
| Root-computation share of commit path | 56.6% | 66.9% | 53.2% |
| Device-movable fraction of the wave (encode+hash) | 96.4% | 94.1% | 77.6% |
| CPU-bound collect/traversal share | 3.6% | 5.9% | 22.4% |
| Dirty nodes (storage + state) | 2,012 | 8,538 | 39,850 |
| Changed-node D2H volume | 255 KB | 1.1 MB | 5.0 MB |
| Transfer: bandwidth term at measured CUDA rates (~21-29GB/s at these sizes, ~46-55GB/s asymptote; latency-dominated below the measured 256KB breakeven) | ~25us | ~60us | ~200us |
| Optimistic net-win bound (of commit path) | 45.8% | 28.5% | 34.7% |

Decision: NO-GO on these numbers. The bound is optimistic to the point of unreliable:
(1) it credits the GPU with displacing SINGLE-CORE hash time, but production uses the
multi-core work-stealing backend (~11x faster on the trie mix), shrinking the real
displaceable time ~an order of magnitude - likely to or below the ~300us dispatch floor;
(2) the commit-path share excludes EVM/execution time - the authoritative block-level
share needs an expb/dotTrace payload run; (3) per-level widths at these shapes sit far
below the 65,536 GPU crossover; (4) the CPU-bound collect/traversal anchor GROWS with
scale (3.6% -> 22.4% of the wave from 100 to 1600 accounts - the state trie's deep DAG
walk stays on the host regardless), and the transfer term only matters at MB-scale dirty
sets. Transfer-cost model (MEASURED, GpuTransferProbe --gpu-transfer-probe): per-copy cost is
piecewise max(fixed latency, size/bandwidth) - fixed latency ~6us on the CUDA RTX 6000,
~67us on the shared-DRAM gfx1036 iGPU; latency/bandwidth breakeven 256KB on both;
asymptotic bandwidth ~46-55GB/s CUDA, ~11-13GB/s iGPU. Many small copies pay a severe
submit tax: 33-187x vs one batched copy of the same bytes. The earlier ~300us end-to-end
dispatch figure decomposes (256KB in/out) on CUDA into ~33us launch/sync + ~47us H2D +
~42us D2H + ~4us kernel (~126us total), so the true launch+sync floor is ~33us CUDA /
~50us iGPU and small-payload transfers are already inside the end-to-end figure -
transfer does not stack on it. Async streams can further overlap the changed-node D2H
with subsequent compute. iGPU note: the standard CopyFromCPU staging copy is largely
pure overhead on a shared-DRAM device - the correct lever there is a host-visible
zero-copy allocation, not pinned staging. Consequence: the device-resident chain's real saving over per-level
dispatch is N_levels x fixed round-trip latency (not bandwidth), and the honest cost of
the changed-node return at mainnet scale is ~250us of overlappable bandwidth. This
strengthens the transfer side of the idea; the decision rests on deflators (1), (2) and
the collect anchor, not on transfer. Reopen only if a block-level dotTrace shows root
computation is a meaningful end-to-end slice AND a real kernel-time model still clears
dispatch+transfer against the PARALLEL CPU baseline; a go additionally requires a second
byte-exact MPT encoder in kernel code with mandatory differential fuzzing against the
CPU encoder.

## (d) Three-environment caveat

- Dev box (this machine): CUDA discrete card + OpenCL iGPU. CUDA is a real win above the
  crossover; the shadow selects the highest-memory non-CPU accelerator (the RTX PRO 6000).
- Benchmarkoor / reproducible-benchmark runners: AMD Ryzen 7 PRO 8700GE with a Radeon 780M
  iGPU (OpenCL only). On iGPU-class hardware GPU offload is a NET REGRESSION in the measured
  range - the OpenCL-iGPU row is what that infrastructure would exhibit with UseGpu enabled.
  Leave UseGpu off there.
- CI runners: assumed GPU-less. GPU backend creation fails cleanly via `TryCreate`, GPU
  tests skip-green (`Assert.Ignore`), and the shadow keeps the CPU backend. No GPU
  dependency executes.

## (e) Validation results

- Recursive-path milestone: full Amsterdam pyspec matrix 954/954 green with
  `BalShadowRootMismatches == 0` and `BalShadowRootErrors == 0`.
- Consensus-safety review: PASSED - full-branch diff review confirmed zero consensus-path
  changes; all lane B code is shadow-only (read-only trie store, never commits, never gates
  block acceptance).
- Batched/GPU-chain run: AmsterdamBlockchainTests with the shadow enabled and the GPU-capable
  backend chain selected (UseGpu=true; GpuMinBatch=65536 keeps every pyspec-sized batch on
  the multi-core CPU backend, GPU dormant). NUnit workers capped at 4, CI env unset, on this
  box (Ryzen 9 9950X, RTX PRO 6000 present). Result: PASSED - 22195/22195 succeeded, 0 failed,
  0 skipped, duration 1h 09m 50s. The in-suite asserts (per-test after draining in-flight lanes,
  and again at fixture teardown) enforce `BalShadowRootMismatches == 0` and
  `BalShadowRootErrors == 0`; all passed, so the shadow lane matched the header state root on
  every processed Amsterdam block through the batched CPU backend. No mismatch, error, or
  self-disable text in the run log.
- Shadow timing (`BalShadowRootLastMicros`): NOT reported as median/p95. This metric is a
  last-write-wins gauge (a single `long` overwritten per computation in `BalStateRootShadow`);
  the codebase records no histogram or percentile aggregation, and the pyspec suite does not
  dump per-block values. Median/p95 would require adding an aggregating collector, which is out
  of scope and would be an instrument-hack; reported honestly as unavailable rather than
  fabricated. (The idle-machine per-batch CPU/wall figures in the contention section are the
  closest measured timing signal for the CPU backend.)

## (f) Promotion recommendation (out of scope, forward-looking)

Promoting the shadow from a counting lane to a consensus lane would require, at minimum:
- A soak proving `Mismatches == 0` and `Errors == 0` over a large, adversarial block corpus
  (mainnet-shadow for weeks + every negative pyspec fixture), since a consensus lane must
  never false-reject.
- Deciding the acceptance rule: Lane B alone cannot accept a block (a lying BAL is only
  caught by Lane A's exec-vs-BAL check); the promoted rule stays "valid iff BOTH lanes
  pass," with Lane B able to REJECT on root mismatch. The failure semantics (which lane's
  verdict is authoritative on disagreement, and how a Lane B error is handled vs a genuine
  mismatch) must be specified so a transient shadow error can never reject a valid block.
- AuRa re-verification: the unconditional EIP-161 deletion assumes `Eip158IgnoredAccount ==
  null`; re-verify on an AuRa BAL chain before enabling there.
- Removing the self-disable / skip behavior (a consensus lane cannot silently skip) and
  bounding worst-case latency so Lane B never becomes the block-processing bottleneck.

## (g) Deferred items

- AmsterdamParallel* / BatchRead* / ParallelFull matrix with the shadow + GPU chain: not run
  in the batched/GPU-chain run (scoped to AmsterdamBlockchainTests only). These fixtures
  already enable the shadow (recursive path) and were part of the 954/954 recursive-path
  matrix; re-running them through the batched/GPU chain is deferred.
- Devnet soak (>= 1000 blocks): NOT RUN. Whether a BAL devnet is currently live is not
  determinable cheaply from the repo. The repo shows BAL devnet branches (bal-devnet-2
  through bal-devnet-7) and a `.claude/bal-devnet-6` scripts directory, indicating BAL
  devnets have existed, but current liveness is UNKNOWN from the repository alone.
- JitAsm inspection of the vertical multi-buffer round function: not done; moot given the
  vertical kernel was not selected for production.
