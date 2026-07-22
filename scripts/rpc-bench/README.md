# RPC benchmarking on the self-hosted runner

This directory holds the scripts behind the
[`run-rpc-benchmarks.yml`](../../.github/workflows/run-rpc-benchmarks.yml) workflow,
which benchmarks an execution client's **state-reading JSON-RPC** (`eth_call`,
`eth_getBalance`, `trace_*`, `debug_*`, …) on the self-hosted
`reproducible-benchmarks` runner, reusing the DB snapshots that the EXPB
reproducible-benchmarks workflow already keeps there. Nethermind is the primary
target; **geth and reth** can also be benchmarked from their same-block snapshot
sets, and a **comparison mode** runs two clients side by side and diffs their
responses.

It can drive three load tools and, optionally, capture a JetBrains dotTrace
performance snapshot of the node (Nethermind only) and post-process it to XML —
the same flow the EXPB workflow uses.

## Goals

1. **A CI to check current node RPC performance** with any of three tools.
2. **The on-disk DB snapshots must never be corrupted** — only reads against them.
3. **dotTrace capture + XML** so RPC-call hot spots can be iterated on quickly.
4. **Cross-client response comparison** — the same requests against Nethermind
   and geth/reth, flagging any response differences (`reference_client`).

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

## Multi-client snapshot sets

Besides the expb Nethermind snapshots, the runner keeps **same-block snapshot
sets** under `/mnt/sda/<client>-<block>` — e.g. `geth-25490000`,
`nethermind-25490000`, `nethermind-flat-25490000`, `reth-25490000` — all
captured at the same head, each carrying provenance sidecars
(`_snapshot_metadata.json`, `_snapshot_web3_clientVersion.json`,
`_snapshot_eth_getBlockByNumber.json`) which `start-node.sh` logs at startup.

The `snapshot_block` input selects a set (Nethermind resolves to
`nethermind[-flat]-<block>` per `state_layout`). It defaults to empty (the expb
snapshot) for Nethermind-only runs, and to `25490000` for geth/reth and for any
comparison run — a comparison **requires both nodes at the same head**, which
the same-block sets guarantee (`assert_same_head` enforces it before any diff).

Per-client node profiles in `start-node.sh`:

| Client | Image default | Datadir handling | Parked-at-head flags |
|---|---|---|---|
| `nethermind` | branch resolution (build/reuse), like before | snapshot = datadir, mounted at `/execution-data` | discovery off, 0 peers, expb stability flags |
| `geth` | `ethereum/client-go:stable` | snapshot holds the contents of `<datadir>/geth` → mounted at `/execution-data/geth` | `--nodiscover --maxpeers=0` |
| `reth` | `ghcr.io/paradigmxyz/reth:latest` | snapshot = datadir (`db/`, `static_files/`), mounted at `/execution-data` | `--disable-discovery --max-*-peers=0` |

geth/reth are compared against, not built from branches — pin their images via
`docker_image` (primary) / `node_config.reference_image` (reference) when the
default mutable tags matter. A snapshot/image version mismatch is visible from
the logged `_snapshot_web3_clientVersion.json` sidecar.

## Comparison mode (`reference_client`)

Setting `reference_client` starts a **second, independently-isolated node**
(own overlay, scratch subtree, fingerprint tripwire, container) from that
client's same-block snapshot on port 8546, then runs the selected tool in its
comparison mode:

- `benchmark_tool=jsonbench` → `runner compare`: one-shot differential test of a
  curated method list; writes `comparison-results.json` +
  `comparison-report.html` and a per-method diff table into the step summary.
  This is the **recommended** comparison path. Failing the job on any diff is
  opt-in (`tool_config.fail_on_diff`) — some differences (error wording, gas
  estimates) are legitimate until the method list is curated per client pair.
- `benchmark_tool=flood` → flood `--equality`: the fork's differential mode
  (`flood all nethermind=… geth=… --equality`); results are captured from
  stdout into the summary.
- `benchmark_tool=ethcallchaos` → rejected in `resolve` (single-node tool).

Both nodes share the runner, so comparison runs measure **correctness**, not
clean latency — for perf A/B use two separate single-node runs.

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
| `benchmark_tool` | `flood`, `ethcallchaos`, or `jsonbench`. |
| `client` | `nethermind` (default), `geth`, or `reth` — the node under test. |
| `reference_client` | `none` (default) or a client to compare against (see comparison mode). |
| `snapshot_block` | Same-block snapshot set tag (`/mnt/sda/<client>-<tag>`); empty = expb snapshot (nethermind) / `25490000` (geth/reth, comparisons). |
| `docker_image` | Optional explicit image for the benchmarked client (skips build/reuse resolution). |
| `dottrace` | Profile the node and post-process to XML. Nethermind only; works with **any** Nethermind image. |
| `state_layout` | `flat` (default) or `halfpath` — picks the Nethermind snapshot + layout flags; ignored for geth/reth. |
| `additional_nethermind_flags` | Extra flags appended to the node command. |
| `tool_config` | Tool-specific JSON (see below). |
| `node_config` | Advanced JSON overrides (see below). |

Image resolution without `docker_image` (Nethermind):
`master`/`performance`/`paprika`/`release/*` reuse the prebuilt
`nethermindeth/nethermind:<branch>` Docker Hub image; any other branch is built
from `Dockerfile` directly on the runner. geth/reth default to their upstream
images (see the table above).

### `node_config` JSON

```json
{
  "db_source": "",                 // snapshot path; empty = resolved from client/state_layout/snapshot_block
  "db_isolation": "overlay",       // overlay | copy | readonly-bind
  "scratch_root": "/mnt/sda/expb-data/rpc-bench-scratch",
  "network": "mainnet",
  "jsonrpc_modules": "Eth,Subscribe,Trace,TxPool,Web3,Proof,Net,Parity,Health,Rpc,Debug",
  "health_timeout_minutes": 30,
  "cpuset": "",                    // e.g. "2-7,10-15" to pin the node like expb does
  "memory": "",                    // e.g. "64g"
  "reference_db_source": "",       // reference snapshot path; empty = resolved from reference_client/snapshot_block
  "reference_image": "",           // reference image; empty = the client's upstream default
  "reference_flags": ""            // extra flags for the reference node's command
}
```

> The load generator runs on the **same machine** as the node, so absolute
> numbers include some co-location contention. Use the workflow for **relative**
> comparisons (branch vs branch, before vs after a change). Pinning the node via
> `node_config.cpuset` (expb uses `2-7,10-15`) improves stability.

## The three tools and their configs

### `flood` — Vegeta load test ([kamilchodola/flood](https://github.com/kamilchodola/flood))

Replays fixed RPC method workloads at increasing request rates and reports
latency/throughput per rate. Same tool/flags as the `is_performance_check` path
of `rpc-comparison.yml`, but against the local snapshot-backed node.

```json
{
  "tests": "eth_call eth_getBalance",   // subset of `flood ls` Single Load Tests; empty = all ('all' in equality mode)
  "rates": "10 100 500",                 // Vegeta request rates (req/s); load mode only
  "duration": 30,                         // seconds per rate; load mode only
  "deep_check": false,                    // pass --deep-check; load mode only
  "label": "",                            // node label; empty = the client name
  "extra_args": ""                        // appended to the flood invocation
}
```

Scope control: `tests` (which methods), `rates`, `duration`. Test names use the
RPC method's camelCase (e.g. `eth_call`, `eth_getBalance`, `eth_getStorageAt`,
`eth_getBlockByNumber`, `eth_feeHistory`) — `flood ls` prints the full list.
With `reference_client`, flood runs `--equality` instead (rates/duration/output
do not apply; flood rejects them in that mode).

### `jsonbench` — [NethermindEth/json-bench](https://github.com/NethermindEth/json-bench)

A Go runner (built on the runner from a pinned commit via its own
`runner/Dockerfile`, which bundles the k6 binary) with two modes, auto-selected
from `reference_client` and overridable via `mode`:

- **benchmark** (single node, or both side by side): k6-driven load benchmark of
  a weighted call mix; produces `results.json` / `results.csv` / `report.html`.
  Metrics come from k6's own `summary.json` — the pinned json-bench builds
  per-client/per-method metrics from it directly, so **no Prometheus is
  involved**. The summary renders an overall + per-method latency table parsed
  from `summary.json`.
- **compare** (needs a reference node): one-shot differential test — each call
  from the compare config goes to both nodes and the responses are diffed.

```json
{
  "ref": "",                                       // json-bench commit/tag; empty = pinned default
  "mode": "",                                      // benchmark | compare; empty = auto
  "benchmark_config": "",                          // workload: bare name | repo-relative path; empty = generated read mix
  "compare_config": "config/compare/defaults.yaml",// repo-relative or absolute runner path
  "rps": "", "duration": "", "vus": "",            // override the workload; empty = keep its values (generated default: 100/60s/10)
  "concurrency": 5, "timeout": 30,                 // compare mode
  "validate_schema": false,                        // compare: also validate against the OpenRPC schema
  "html_report": true,
  "fail_on_diff": false,                           // compare: fail the job on any response difference
  "extra_args": ""
}
```

`benchmark_config` accepts json-bench's curated head-only workloads by name —
`realistic-mix-head` (weighted mainnet mix), `ethcall-contracts-head` (`eth_call`
across 27 contracts), `new-state-methods-head` (`eth_estimateGas`/`eth_getCode`/
`eth_getProof`/`eth_getStorageAt` + `eth_call`) — or any repo-relative/absolute
path. These target `latest`, so they run against the snapshot head. The script
**rewrites the config's `clients:` list** to the node(s) started here (so the
repo's five-client configs work as-is) and injects a loose per-call threshold so
k6 emits per-method sub-metrics into `summary.json`. The config's relative
`./rpc-calls/*.jsonl` fixtures resolve via the container's working directory
(the mounted checkout at `/jb`) — json-bench's loader rejects absolute paths.

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

When `dottrace=true` (requires `client=nethermind` — dotTrace is .NET-specific) —
using the same mechanism as expb's `--dottrace`, so it works with **any**
Nethermind image (no special diag build):

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

> The capture runs in dotTrace's default **sampling** mode (no
> `--profiling-type` is passed) — deliberately, since JetBrains' Reporter.exe
> cannot post-process Timeline snapshots into XML. The snapshot spans the
> node's whole lifetime, including DB load and warmup. Keep the benchmark
> `duration` the dominant phase, or analyze by time window, so RPC-call frames
> dominate the captured `OwnTime`.

Download the `dottrace-reports` artifact and inspect locally:

```bash
scripts/dottrace-report.sh top   reports/<name>-report.xml 30
scripts/dottrace-report.sh compare reports/before.xml reports/after.xml 30
```

## Runner prerequisites

The `reproducible-benchmarks` self-hosted runner must provide:

- **Docker** (the nodes run as containers; EthCallChaos runs in a .NET SDK
  container; json-bench builds and runs its own runner image).
- **The expb DB snapshots** (`/mnt/sda/nethermind-flat-snapshot`, …) and, for
  geth/reth or comparison runs, the **same-block snapshot sets**
  (`/mnt/sda/<client>-<block>`, e.g. `geth-25490000`).
- **A writable scratch location** on the same large disk (default
  `/mnt/sda/expb-data/rpc-bench-scratch`).
- **`mount`/`umount` privileges** and overlayfs (expb already uses both there).
- **`jq`, `curl`, `git`**, **`python3` + `pip`** (flood; json-bench also renders
  its benchmark config via `python3` + PyYAML), and the **.NET SDK** (only if
  `/opt/dottrace` is not already installed by previous expb dotTrace runs).

## Files

| File | Role |
|---|---|
| `lib.sh` | Shared helpers: logging, path guards, RPC health wait, head-match assert, DB fingerprint tripwire. |
| `start-node.sh` | Fingerprint baseline → isolate DB → start container (per-client profile, primary/reference instance) → wait for RPC. |
| `stop-node.sh` | Graceful stop → collect logs + dotTrace → **verify snapshot unchanged** → tear down (per instance via `NODE_ENV_FILE`). |
| `run-flood.sh` | Install flood + Vegeta, run the selected tests (load or `--equality`), report. |
| `run-ethcallchaos.sh` | Clone/build/run EthCallChaos in an SDK container, scrape its API. |
| `run-jsonbench.sh` | Clone/build json-bench's runner image, adapt the workload config to the node(s), run `benchmark` (summary.json metrics, no Prometheus) or `compare`, report. |
| `cleanup.sh` | Guarded defensive cleanup (stale containers, leftover mounts, scratch). |
