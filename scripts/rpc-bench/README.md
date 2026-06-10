# RPC benchmarking on the self-hosted runner

This directory holds the scripts behind the
[`run-rpc-benchmarks.yml`](../../.github/workflows/run-rpc-benchmarks.yml) workflow,
which benchmarks a Nethermind node's **state-reading JSON-RPC** (`eth_call`,
`eth_getBalance`, `trace_*`, `debug_*`, …) on the self-hosted
`reproducible-benchmarks` runner, reusing the DB snapshots that the EXPB
reproducible-benchmarks workflow already keeps there.

It can drive two different load tools and, optionally, capture a JetBrains
dotTrace timeline of the node and post-process it to XML — the same flow the EXPB
workflow uses.

## Goals

1. **A CI to check current node RPC performance** with either of two tools.
2. **The on-disk DB snapshot must never be corrupted** — only reads against it.
3. **dotTrace capture + XML** so RPC-call hot spots can be iterated on quickly.

## Alignment with expb

Path selection and node startup deliberately mirror expb
(`execution-payloads-benchmarks`), which uses the same snapshots on this runner:

- The snapshot is a Nethermind **datadir** (contains `<network>/` chain DB). It is
  bound to `/execution-data` and the node runs
  `--datadir=/execution-data --Init.BaseDbPath=<network>` — same as expb's
  `NethermindConfig`.
- `state_layout=flat` → `/mnt/sda/nethermind-flat-snapshot` + `--FlatDb.Enabled=true`
  (the `snapshot_source` of `github-action-mainnet-flat.yaml`);
  `halfpath` → `/mnt/sda/nethermind-snapshot`. Override via `node_config.db_source`.
- The default `overlay` isolation is the same mechanism as expb's
  `snapshot_backend: overlay`, including the `redirect_dir=on,metacopy=on,volatile`
  mount options (with a plain-options fallback).
- The node gets expb's stability flags (`--Init.DiscoveryEnabled=false`,
  `--Network.MaxActivePeers=0`, `--Merge.SweepMemory=NoGC`,
  `--Merge.CompactMemory=No`, `--Merge.CollectionsPerDecommit=-1`,
  `--Pruning.Mode=None`) and env (`DOTNET_TieredCompilation=0`,
  `DOTNET_GCLatencyLevel=0`).

## How the node is started (and why the snapshot is safe)

The pristine snapshot at `db_source` is **never mounted writable**. `start-node.sh`
builds an isolated, writable *view* of it and gives the container only that view:

| `db_isolation` | Mechanism | Snapshot protection |
|---|---|---|
| `overlay` (default) | `mount -t overlay` with the snapshot as a **read-only `lowerdir`** and scratch as `upperdir`/`workdir`; the container gets the merged dir. All writes land in the scratch upper layer. | Kernel-enforced — the lowerdir is read-only. |
| `copy` | `cp -a --reflink=auto` the snapshot to scratch (instant CoW clone on btrfs/xfs, full copy otherwise); the container gets the copy. | The node never sees the original at all. |
| `readonly-bind` | Read-only bind mount of the snapshot, passed `:ro` into the container. | Advanced — requires the node/RocksDB to open the DB read-only, which it may refuse. Prefer `overlay`. |

### Tamper tripwire (active verification of goal #2)

`start-node.sh` records a **fingerprint** of the snapshot before the run and
`stop-node.sh` recomputes it after. The fingerprint is a full recursive listing
(path, type, size, mtime, mode, owner, symlink target) plus a sha256 of the
small RocksDB control files that get rewritten the instant a DB is opened
read-write (`CURRENT`, `IDENTITY`, `MANIFEST-*`, `OPTIONS-*`). If anything
differs, **the job fails**. Hashing is limited to the small control files so the
check stays fast on a multi-TB DB; listing errors are fatal rather than
silently producing a partial fingerprint. After a clean verification the
fingerprint is persisted (`<scratch_root>/fingerprints/`) as a **cross-run
anchor** — the next run warns if the snapshot changed in between (e.g. during a
hard-interrupted run whose verify step never executed).

Path safety is layered: the `resolve` job validates `db_source`/`scratch_root`
shape, and every script canonicalizes them (`realpath`, symlink-proof), rejects
shallow paths, enforces disjointness, and refuses any recursive delete while
something is still mounted underneath ([`cleanup.sh`](cleanup.sh) applies the
same guards in the workflow's defensive-cleanup step).

## Workflow inputs

| Input | Meaning |
|---|---|
| `benchmark_tool` | `flood` or `ethcallchaos`. |
| `docker_image` | Optional explicit image (skips build/reuse resolution). |
| `dottrace` | Profile the node and post-process to XML. Works with **any** image. |
| `state_layout` | `flat` (default) or `halfpath` — picks snapshot + layout flags. |
| `additional_nethermind_flags` | Extra flags appended to the node command. |
| `tool_config` | Tool-specific JSON (see below). |
| `node_config` | Advanced JSON overrides (see below). |

Image resolution without `docker_image`: `master`/`performance`/`paprika`/`release/*`
reuse the prebuilt `nethermindeth/nethermind:<branch>` Docker Hub image; any other
branch is built from `Dockerfile` directly on the runner.

### `node_config` JSON

```json
{
  "db_source": "",                 // snapshot path; empty = resolved from state_layout
  "db_isolation": "overlay",       // overlay | copy | readonly-bind
  "scratch_root": "/mnt/sda/expb-data/rpc-bench-scratch",
  "network": "mainnet",
  "jsonrpc_modules": "Eth,Subscribe,Trace,TxPool,Web3,Personal,Proof,Net,Parity,Health,Rpc,Debug,Admin",
  "health_timeout_minutes": 30,
  "cpuset": "",                    // e.g. "2-7,10-15" to pin the node like expb does
  "memory": ""                     // e.g. "64g"
}
```

> The load generator runs on the **same machine** as the node, so absolute
> numbers include some co-location contention. Use the workflow for **relative**
> comparisons (branch vs branch, before vs after a change). Pinning the node via
> `node_config.cpuset` (expb uses `2-7,10-15`) improves stability.

## The two tools and their configs

### `flood` — Vegeta load test ([kamilchodola/flood](https://github.com/kamilchodola/flood))

Replays fixed RPC method workloads at increasing request rates and reports
latency/throughput per rate. Same tool/flags as the `is_performance_check` path
of `rpc-comparison.yml`, but against the local snapshot-backed node.

```json
{
  "tests": "eth_call eth_getBalance",   // subset of `flood ls` Single Load Tests; empty = all
  "rates": "10 100 500",                 // Vegeta request rates (req/s)
  "duration": 30,                         // seconds per rate
  "deep_check": false,                    // pass --deep-check
  "label": "nethermind",
  "extra_args": ""                        // appended to the flood invocation
}
```

Scope control: `tests` (which methods), `rates`, `duration`. Test names use the
RPC method's camelCase (e.g. `eth_call`, `eth_getBalance`, `eth_getStorageAt`,
`eth_getBlockByNumber`, `eth_feeHistory`) — `flood ls` prints the full list.

### `ethcallchaos` — [kamilchodola/EthCallChaos](https://github.com/kamilchodola/EthCallChaos)

An ASP.NET app (no CLI) that hammers `eth_call` and ranks the slowest cases. It
is launched in a .NET SDK container, configured via env vars, run for a fixed
duration, then its HTTP API (`/api/stats`, `/api/leaderboard`) is scraped.

```json
{
  "ref": "master",        // branch/tag of EthCallChaos to build
  "corpus_db": "",         // optional path ON THE RUNNER to a pristine corpus DB (copied, not mutated)
  "corpus_url": "",        // optional URL override for the corpus download
  "rate": 50,              // Rpc:MaxCallsPerSecond
  "parallel": 8,           // Rpc:MaxParallelCalls
  "duration": 300,         // seconds of load
  "leaderboard_top": 50,   // rows scraped from /api/leaderboard
  "api_port": 5000,
  "min_mean_ms": 1,        // Validation:MinMeanThresholdMs (tool default 200 keeps the leaderboard empty against a fast local node)
  "max_cv": 10             // Validation:MaxCoefficientOfVariation (tool default 0.3 rejects sub-ms loopback measurements)
}
```

Scope control: `rate`, `parallel`, `duration`, and the corpus (which
contracts/calls are exercised). EthCallChaos has no built-in per-method filter;
the corpus DB is how you constrain the workload. Corpus resolution order:
`corpus_db` (runner-local path, copied to scratch) → `corpus_url` (defaults to
the `corpus-v1` release asset of `kamilchodola/EthCallChaos`) → a DB committed
in the tool repo → fresh evolution from scratch.

## dotTrace flow (goal #3)

When `dottrace=true` — using the same mechanism as expb's `--dottrace`, so it
works with **any** image (no special diag build):

1. The host-installed dotTrace CLI (`/opt/dottrace`, installed on demand via
   `dotnet tool install JetBrains.dotTrace.GlobalTools`) is mounted read-only
   into the container, and the entrypoint is wrapped:
   `dottrace start --framework=NetCore --save-to=... --propagate-exit-code -- /nethermind/nethermind …`.
2. `stop-node.sh` stops the container with **SIGINT**, letting dotTrace finalize
   the `.dtp` snapshot into the mounted diag dir.
3. The `generate-dottrace-reports` job (Windows) runs `Reporter.exe` to convert
   each `.dtp` to XML — identical to the EXPB workflow.
4. The `dottrace-summary` job runs [`scripts/dottrace-report.sh`](../dottrace-report.sh)
   `top` over each XML and writes the hot functions into the job summary.

> The timeline snapshot spans the node's whole lifetime, including DB load and
> warmup. Keep the benchmark `duration` the dominant phase, or analyze by time
> window, so RPC-call frames dominate the captured `OwnTime`.

Download the `dottrace-reports` artifact and inspect locally:

```bash
scripts/dottrace-report.sh top   reports/<name>-report.xml 30
scripts/dottrace-report.sh compare reports/before.xml reports/after.xml 30
```

## Runner prerequisites

The `reproducible-benchmarks` self-hosted runner must provide:

- **Docker** (the node runs as a container; EthCallChaos runs in a .NET SDK container).
- **The expb DB snapshots** (`/mnt/sda/nethermind-flat-snapshot`, …).
- **A writable scratch location** on the same large disk (default
  `/mnt/sda/expb-data/rpc-bench-scratch`).
- **`mount`/`umount` privileges** and overlayfs (expb already uses both there).
- **`jq`, `curl`, `git`**, **`python3` + `pip`** (flood), and the **.NET SDK**
  (only if `/opt/dottrace` is not already installed by previous expb dotTrace runs).

## Files

| File | Role |
|---|---|
| `lib.sh` | Shared helpers: logging, path guards, RPC health wait, DB fingerprint tripwire. |
| `start-node.sh` | Fingerprint baseline → isolate DB → start container → wait for RPC. |
| `stop-node.sh` | Graceful stop → collect logs + dotTrace → **verify snapshot unchanged** → tear down. |
| `run-flood.sh` | Install flood + Vegeta, run the selected tests, report. |
| `run-ethcallchaos.sh` | Clone/build/run EthCallChaos in an SDK container, scrape its API. |
| `cleanup.sh` | Guarded defensive cleanup (stale containers, leftover mounts, scratch). |
