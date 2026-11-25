# EIP-8288 builder-aggregation and mempool benchmarks

Two scenarios over the eth/76 stack, both runnable on any x86-64 or arm64 Linux host with docker, and on an ephemeral GCE VM
through `run-eip8288-calibration.yml` with `suite=nethermind-bench`.

- **B, builder aggregation**: a builder folds the fresh leanSPHINCS signatures of N mode-0 wrappers (at most
  `MAX_LEANSIG_DEPS_PER_WRAPPER` = 16 each) into the block's single dependency proof, in one hop, through the production
  `DependencyProofAggregator.Prove` path with a fresh `DependencyProofCache` per repeat. Reported per N: proving wall time,
  process CPU (cores busy), peak RSS of that repeat (`VmHWM` reset through `clear_refs`), native proving calls (leaves and
  merges), proof size, verification of the final proof (median of five), and the time to check the N wrapper signatures.
  A set fits the slot when its slowest repeat is within `SLOT_MS - RESERVE_MS`, by default 12 s less 4 s reserved for
  building, executing and propagating the block.
- **A, mempool**: `NODES` Nethermind containers, each pinned to its own `NODE_CPUS` cpuset with a `NODE_MEMORY` limit and
  tc netem on its egress, peered statically to about `DEGREE` others, take a Poisson stream of mode-0 wrappers through
  `eth_sendProofWrapper` at random nodes. Transactions have mixed dependency counts, a fraction of claims comes from a shared
  hot set, and a fraction of wrappers is also submitted to a second node, so nodes see duplicate announcements and overlapping
  claims. Each node's `newPendingTransactions` subscription times arrival; a wrapper has reached a node once all its
  transactions are pending there. Reported: propagation latency p50/p95/p99 from first submission, coverage, per-node CPU,
  memory peak and wire bytes (cgroup and `/proc/net/dev` deltas), mempool aggregation proofs and their mean proving time,
  wrapper fetches per announcement, compact-reference hit rate, and exception lines in the node logs. The run is repeated
  `MEMPOOL_RUNS` times for the eth/76 build and for a `BASELINE_REF` (`feature/eip8288-eth75`) build, alternating arms, and
  each arm gets mean and CV across runs.

## Profiles

| profile | resources | used for |
|---|---|---|
| builder | the whole VM, unconstrained (`c3-standard-88`: 88 vCPU Sapphire Rapids, 352 GB) | B |
| average validator | 8 vCPU (4 cores with SMT) cpuset, 16 GB; 40±10 ms one-way per link, 25 Mbit/s upload | B for contrast, and every node of A |

Upload is the binding link of a home connection, so netem shapes each node's egress only: every packet is delayed and
rate-limited once, at its sender. Change them with `AVERAGE_CPUS`, `AVERAGE_MEMORY`, `DELAY_MS`, `JITTER_MS`, `UP_MBIT` and
`LOSS_PCT`.

## Running

On a Linux host with docker (needs `(NODES + 1) * NODE_CPUS` vCPUs for A):

```sh
scripts/eip8288-bench/run.sh /tmp/eip8288-bench builder,mempool
COUNTS=20,200 REPEATS=5 scripts/eip8288-bench/run.sh /tmp/eip8288-bench builder   # B only
NODES=6 RATE=5 SECONDS_TO_RUN=120 MEMPOOL_RUNS=3 scripts/eip8288-bench/run.sh /tmp/eip8288-bench mempool   # A only
```

On GCP, once `run-eip8288-calibration.yml` is on `master`:

```sh
gh workflow run run-eip8288-calibration.yml -f suite=nethermind-bench -f machine_type=c3-standard-88 \
  -f nethermind_ref=feature/eip8288-eth76-benchmarks -f bench_scenarios=builder,mempool \
  -f bench_env="COUNTS=20,100,200,300,500 REPEATS=5 MEMPOOL_RUNS=3"
```

`run.sh` publishes this tool in an SDK container, builds the eth/76 image from the checkout and the eth/75 image from
`BASELINE_REF`, and writes `summary.md` plus, per scenario, machine-readable results: `builder/<profile>/builder.{json,csv,md}`,
`mempool/<arm>/run<i>/{results.json,nodes.csv,wrappers.csv,arrivals.csv,summary.md}` with the raw node snapshots and logs, and
`mempool/<arm>/aggregate.{json,md}`. Every JSON records the host, cgroup limits, Nethermind commit and Nethermind.Crypto.Lean
version, ABI and aggregated key.

Defaults: B runs N = 20, 100, 200, 300, 500 with one warm-up and five measured repeats; A runs 6 nodes, 5 wrappers/s for 120 s
after a 20 s warm-up and 30 s of grace, transactions carrying 1/2/4/8 dependencies with weights 60/25/10/5, wrappers carrying
1/2/4 transactions with weights 80/15/5, 10% shared claims and 10% duplicate submissions. The tool's subcommands take the same
settings as `--name=value` options; see `Program.cs`.

## Limits

- Signatures come from the package's `fixtures` tool, which signs under one test key; claims differ by message. The guest binds
  each (message, key) claim alike, so per-signature work does not depend on key reuse. Signing is outside every timed section.
- A has no consensus client: heads stay at genesis, so nodes run with `TxPool.AcceptTxWhenNotSynced` and transactions are never
  included. Every transaction comes from its own funded account at nonce 0, so admission never waits on nonce order, and the
  pool only grows over a run. Blocks, and with them builder proving inside the nodes, are out of scope here; B measures that
  proving directly.
- Latency includes the subscription's local websocket delivery, a few milliseconds on one host.
- Percentiles are nearest-rank; with fewer than twenty samples p95 and p99 are the maximum.
