# RPC benchmarking on the self-hosted runner

Scripts behind the [`run-rpc-benchmarks.yml`](../../.github/workflows/run-rpc-benchmarks.yml)
workflow, which benchmarks Nethermind's **state-reading JSON-RPC**
(`eth_call`, `eth_getBalance`, `trace_*`, `debug_*`, …) on a self-hosted
benchmark runner, reusing the EXPB workflow's DB snapshots.
Drives three load tools and can optionally capture a JetBrains dotTrace snapshot
and post-process it to XML.

**Which runner, and what it can serve.** The `arch` input picks the box:
`amd64` (default) is `reproducible-benchmarks` with snapshots under `/mnt/sda`,
`arm64` is `reproducible-benchmarks-arm` with snapshots under `/data`. Every
path below follows that choice. **Never compare timings across the two boxes.**

The amd64 box holds the full snapshot set, so it serves every `client`,
`reference_client` and `state_layout`. The arm64 box carries exactly one kind of
snapshot set — Nethermind in the **flat** layout — so there `client`,
`reference_client` and `state_layout` are held to `nethermind` / `none` / `flat`,
and an image it would have to build is refused as well (that box's ~19G root disk
dies under a build). `resolve` checks those limits against the selected runner.

Independently of the runner, **sweep mode** (`jsonbench-sweep`) resolves one
Nethermind flat snapshot and varies only the image, so `run-rpc-sweep.sh` refuses
a non-Nethermind entry in `tool_config.clients`
instead of those inputs. `start-node.sh` stays client-generic, so re-enabling
geth/reth or a second layout is a matter of provisioning the snapshot set and
widening those two guards.

## Goals

1. **A CI to check current node RPC performance** with any of three tools.
2. **The on-disk DB snapshots must never be corrupted** — only reads against them.
3. **dotTrace capture + XML** so RPC-call hot spots can be iterated on quickly.
4. **Response comparison between two builds** — the same requests against two
   Nethermind images, flagging any response differences (the `ctype@image`
   sweep form, and the corpus parity gate below).

## Alignment with expb

Path selection and node startup mirror expb (`execution-payloads-benchmarks`),
which uses the same snapshots on this runner:

- The snapshot is a Nethermind **datadir** (contains `<network>/` chain DB),
  bound to `/execution-data`; the node runs
  `--datadir=/execution-data --Init.BaseDbPath=<network>` — same as expb's
  `NethermindConfig`.
- `state_layout=flat` → `<snapshot root>/nethermind-flat-<block>` +
  `--FlatDb.Enabled=true` (the `snapshot_source` of
  `github-action-mainnet-flat.yaml`). Override via `node_config.db_source`.
- The default `overlay` isolation matches expb's `snapshot_backend: overlay`,
  including `redirect_dir=on,metacopy=on,volatile` mount options (plain-options
  fallback).
- The node is isolated from network and pruning noise (`--Init.DiscoveryEnabled=false`,
  `--Network.MaxActivePeers=0`, `--Pruning.Mode=None`) but otherwise runs
  production defaults — no GC or `DOTNET_*` overrides — so JIT warm-up lands
  inside the measured window; treat a run's first test/rate as warm-up. One-off
  code-gen experiments: set `NODE_ENV_VARS`.

## Snapshot sets

The runner keeps **block-tagged snapshot sets** under
`<snapshot root>/nethermind-flat-<block>` — e.g. `nethermind-flat-25490000` —
each carrying provenance sidecars (`_snapshot_metadata.json`,
`_snapshot_web3_clientVersion.json`, `_snapshot_eth_getBlockByNumber.json`) that
`start-node.sh` logs at startup.

`snapshot_block` selects a set and defaults to `25490000`. Every node in a run
shares one set, so any two builds being compared are at the same head by
construction (`assert_same_head` still enforces it before a diff, and
`corpus_parity.py` refuses to compare two replays whose head block hashes
differ).

Per-client node profiles in `start-node.sh`. Only the `nethermind` row is
reachable on the current runner (see **Supported configuration** above); the
others are kept because they are what a future geth/reth snapshot set would
launch through, and they are unchanged:

| Client | Image default | Datadir handling | Parked-at-head flags |
|---|---|---|---|
| `nethermind` | branch resolution (build/reuse), like before | snapshot = datadir, mounted at `/execution-data` | discovery off, 0 peers, pruning off |
| `geth` | `ethereum/client-go:stable` | snapshot holds the contents of `<datadir>/geth` → mounted at `/execution-data/geth` | `--nodiscover --maxpeers=0` |
| `reth` | `ghcr.io/paradigmxyz/reth:latest` | snapshot = datadir (`db/`, `static_files/`), mounted at `/execution-data` | `--disable-discovery --max-*-peers=0` |

geth/reth are compared against, not built from branches — pin their images via
`docker_image` (primary) / `node_config.reference_image` (reference) when the
default mutable tags matter. A snapshot/image version mismatch is visible from
the logged `_snapshot_web3_clientVersion.json` sidecar.

## Comparison mode (`reference_client`)

Setting `reference_client` starts a **second, independently-isolated node** (own
overlay, scratch subtree, fingerprint tripwire, container) from that client's
same-block snapshot on port 8546, then runs the selected tool's comparison mode:

- `benchmark_tool=jsonbench` → `runner compare`: one-shot differential test of a
  curated method list; writes `comparison-results.json` +
  `comparison-report.html` and a per-method diff table into the step summary.
  **Recommended** comparison path. Failing the job on any diff is opt-in
  (`tool_config.fail_on_diff`) — some differences (error wording, gas estimates)
  are legitimate until the method list is curated per client pair.
- `benchmark_tool=flood` → flood `--equality`
  (`flood all nethermind=… geth=… --equality`); results captured from stdout.
- `benchmark_tool=ethcallchaos` → rejected in `resolve` (single-node tool).

Both nodes share the runner, so comparison runs measure **correctness**, not
clean latency — for perf A/B use two separate single-node runs.

## How the node is started (and why the snapshot is safe)

Under `overlay`/`copy`/`readonly-bind` the pristine snapshot at `db_source` is
**never mounted writable** — `start-node.sh` builds an isolated, writable *view*
and gives the container only that view. `direct` is the exception: it mounts the
snapshot itself read-write and accepts the node's startup writes.

| `db_isolation` | Mechanism | Snapshot protection |
|---|---|---|
| `overlay` (default for nethermind/geth) | `mount -t overlay` with the snapshot as a **read-only `lowerdir`** and scratch as `upperdir`/`workdir`; the container gets the merged dir. All writes land in the scratch upper layer. | Kernel-enforced — the lowerdir is read-only. |
| `copy` | `cp -a --reflink=auto` the snapshot to scratch (instant CoW clone on btrfs/xfs, full copy otherwise); the container gets the copy. | The node never sees the original at all. |
| `readonly-bind` | Read-only bind mount of the snapshot, passed `:ro` into the container. | Advanced — requires the node/DB engine to open the DB read-only, which all three node commands refuse (they open read-write and take a lock). Effectively unusable; prefer `overlay`. |
| `direct` (default for reth) | Bind-mounts the snapshot **read-write** into the container — no overlay, no copy. The node opens the DB in place. | **None — the snapshot is mutated.** |

### Why reth defaults to `direct`

Nodes open their DB engine **read-write** on startup even to serve read-only RPC
(no read-only node mode exists in nethermind/geth/reth). For RocksDB (nethermind)
and Pebble (geth) that touches a handful of tiny lock/control files, so
`overlay`'s copy-up is trivial. reth's MDBX is a **single large `mdbx.dat`**, and
the first write forces overlayfs to copy the *entire* file up before startup
proceeds — **~200 s** on the mainnet snapshot vs ~11–15 s for nethermind/geth.
`direct` writes in place, so reth opens in seconds. The cost: reth's snapshot is
no longer byte-identical — acceptable for read-only benchmarks (no transactions,
no `newPayload`), where the only writes are engine startup housekeeping.

**`direct` caveats:** (1) the tamper tripwire records the diff and warns instead
of failing (below); (2) never point two nodes at the same `direct` snapshot
concurrently (DB lock conflict) — comparison runs are fine, each client uses its
own snapshot; (3) if a snapshot is shared with another consumer (e.g. expb reuses
the nethermind sets), don't put that client on `direct` — hence nethermind/geth
stay on `overlay`.

### Tamper tripwire (active verification of goal #2)

`start-node.sh` fingerprints the snapshot before the run; `stop-node.sh`
recomputes it after. The fingerprint is a full recursive listing (path, type,
size, mtime, mode, owner, symlink target) plus a sha256 of the small RocksDB
control files rewritten the instant a DB is opened read-write (`CURRENT`,
`IDENTITY`, `MANIFEST-*`, `OPTIONS-*`). Any difference **fails the job** — except
under `direct`, where changes are expected: it warns, logs the changed-line
count, and does not update the cross-run anchor. Hashing only the control files
keeps the check fast on a multi-TB DB; listing errors are fatal rather than
producing a partial fingerprint. After a clean verify the fingerprint persists
(`<scratch_root>/fingerprints/`) as a **cross-run anchor** — the next run warns
if the snapshot changed in between (e.g. a hard-interrupted run whose verify
never ran).

Path safety is layered: `resolve` validates `db_source`/`scratch_root` shape, and
every script canonicalizes them (`realpath`, symlink-proof), rejects shallow
paths, enforces disjointness, and refuses any recursive delete while something is
still mounted underneath ([`cleanup.sh`](cleanup.sh) applies the same guards in
the workflow's defensive-cleanup step).

## Workflow inputs

| Input | Meaning |
|---|---|
| `benchmark_tool` | `flood`, `ethcallchaos`, `jsonbench`, or `jsonbench-sweep`. |
| `client` | `nethermind` — the only client with a snapshot set on this runner. |
| `reference_client` | `none` — cross-client comparison needs a second client's snapshot, which this runner does not carry. Compare two Nethermind builds with a `jsonbench-sweep` instead. |
| `arch` | Benchmark runner: `amd64` (default, `/mnt/sda`) or `arm64` (`/data`). Drives every path. |
| `snapshot_block` | Snapshot set tag (`<snapshot root>/nethermind-flat-<tag>`); empty = `25490000`. |
| `docker_image` | Optional explicit image for the benchmarked client (skips build/reuse resolution). |
| `dottrace` | `false` (default), `sampling`, `tracing`, or `timeline` — profiling mode for the node. Works with **any** Nethermind image. `sampling`/`tracing` are post-processed to XML; `timeline` is a UI-only snapshot. `true` is a legacy alias for `sampling`. |
| `state_layout` | `flat` — the only layout with a snapshot set on this runner. |
| `additional_nethermind_flags` | Extra flags appended to the node command. |
| `tool_config` | Tool-specific JSON (see below). |
| `node_config` | Advanced JSON overrides (see below). |

Image resolution without `docker_image`:
`master`/`performance`/`paprika`/`release/*` reuse the prebuilt
`nethermindeth/nethermind:<branch>` Docker Hub image; any other branch is built
from `Dockerfile` on the runner.

### `node_config` JSON

```json
{
  "db_source": "",                 // snapshot path; empty = resolved from snapshot_block
  "db_isolation": "",              // overlay | copy | readonly-bind | direct; empty = overlay
  "scratch_root": "",   // empty = <expb data dir>/rpc-bench-scratch on the selected runner
  "network": "mainnet",
  "jsonrpc_modules": "Eth,Subscribe,Trace,TxPool,Web3,Proof,Net,Parity,Health,Rpc,Debug",
  "health_timeout_minutes": 30,
  "cpuset": "",                    // e.g. "2-7,10-15" to pin the node like expb does
  "memory": "",                    // e.g. "64g"
  "cpu_max_freq_khz": "",          // scaling_max_freq cap for the whole job; empty = 3800000 on amd64, none on arm64; 0 = no cap
  "reference_db_source": "",       // reference-node keys: unreachable while reference_client is pinned to `none`
  "reference_image": "",
  "reference_flags": "",
  "reference_db_isolation": ""
}
```

> The load generator runs on the **same machine** as the node, so absolute
> numbers include co-location contention. Use the workflow for **relative**
> comparisons (branch vs branch, before vs after). Pinning via
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
  Metrics come from k6's own `summary.json` (**no Prometheus** — the pinned
  json-bench builds per-client/per-method metrics from it directly), rendered as
  an overall + per-method latency table.
- **compare** (needs a reference node): one-shot differential test — each call
  from the compare config goes to both nodes and the responses are diffed.

```json
{
  "ref": "",                                       // json-bench commit/tag; empty = pinned default
  "mode": "",                                      // benchmark | compare; empty = auto
  "benchmark_config": "",                          // workload: bare name | repo-relative path; empty = generated read mix
  "compare_config": "config/compare/defaults.yaml",// repo-relative (absolute host paths are copied into the checkout — the loader rejects absolute paths)
  "rps": "", "duration": "", "vus": "",            // override the workload; empty = keep its values (generated default: 100/60s/10)
  "concurrency": 5, "timeout": 30,                 // compare mode
  "validate_schema": false,                        // compare: also validate against the OpenRPC schema
  "html_report": true,
  "fail_on_diff": false,                           // compare: fail the job on any response difference
  "max_fail_rate_pct": 1,                          // benchmark: fail when summary.json's http fail rate exceeds this % (k6 itself exits 0 even at 100%)
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

An ASP.NET app (no CLI) that hammers `eth_call` and ranks the slowest cases.
Launched in a .NET SDK container, configured via env vars, run for a fixed
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

## Triggering

The workflow inputs are plain fields; the default preset (`benchmark_tool: corpus-ab`) needs no JSON
at all. `docker_image` is the image under test (empty = this branch: the prebuilt tag for
master/release, otherwise a build on the runner — refused on arm64); `baseline_image` is the other
arm and the parity reference — its default `cache` is the master baseline recorded after the last
master push, so a PR run executes only the PR image. Everything the inputs do not cover is a
`tool_config` / `node_config` JSON override merged on top (its keys win).

```bash
# This branch vs the cached master baseline on the private corpus — the standard check, all defaults
gh workflow run run-rpc-benchmarks.yml --ref <branch>

# Real two-arm A/B in one job (master runs here too); add -f rounds=2 for A B B A with an in-run A/A control
gh workflow run run-rpc-benchmarks.yml --ref <branch> -f baseline_image=nethermindeth/nethermind:master-<sha>

# Re-record the master baseline by hand (normally automatic after every master push)
gh workflow run run-rpc-benchmarks.yml --ref master -f benchmark_tool=corpus-baseline -f docker_image=nethermindeth/nethermind:master-<sha>

# Same with pinned images (mandatory on arm64, and the fast path everywhere: no build on the runner)
gh workflow run run-rpc-benchmarks.yml --ref <branch> -f arch=arm64 \
  -f docker_image=nethermindeth/nethermind:<pr-tag> -f baseline_image=nethermindeth/nethermind:master-<sha>

# Smoke test of the harness itself (~10 min): tiny cells, one round, short warm-up
gh workflow run run-rpc-benchmarks.yml --ref <branch> -f docker_image=nethermindeth/nethermind:master-<sha> \
  -f requests_per_cell=3000 -f rounds=1 -f timings_passes=4 -f warmup=30

# Rate ladder on the corpus (one cell per rate, still request-sized)
gh workflow run run-rpc-benchmarks.yml --ref <branch> -f rps="50 100 300"

# Two images on a json-bench workload instead of the corpus (timed cells)
gh workflow run run-rpc-benchmarks.yml --ref <branch> -f benchmark_tool=jsonbench-sweep -f rps="50 100" -f duration=60 \n  -f baseline_image=nethermindeth/nethermind:master

# Single node: json-bench curated workload / flood / EthCallChaos
gh workflow run run-rpc-benchmarks.yml --ref <branch> -f benchmark_tool=jsonbench -f rps=50 -f duration=60 \
  -f tool_config='{"benchmark_config":"ethcall-contracts-head"}'
gh workflow run run-rpc-benchmarks.yml --ref <branch> -f benchmark_tool=flood -f rps="10 100 500" -f duration=30 \
  -f tool_config='{"tests":"eth_call eth_getBalance"}'
gh workflow run run-rpc-benchmarks.yml --ref <branch> -f benchmark_tool=ethcallchaos -f rps=50 -f duration=300

# Cross-client response comparison (amd64 only): Nethermind vs reth, same head
gh workflow run run-rpc-benchmarks.yml --ref <branch> -f benchmark_tool=jsonbench -f reference_client=reth

# Profile the node under the corpus load
gh workflow run run-rpc-benchmarks.yml --ref <branch> -f benchmark_tool=jsonbench -f dottrace=sampling \
  -f tool_config='{"eth_call_corpus":true,"corpus_file":"/mnt/sda/expb-data/rpc-bench/eth-call-corpus-20260805T104605Z-497-safe.jsonl.gz"}'
```

Labeling a PR `rpc-benchmark` runs the default preset for that PR's branch.

### The cached master baseline

Every master push that touches `src/Nethermind/**` publishes `nethermindeth/nethermind:master-<sha7>`;
when that `Publish Docker image` run completes, a `workflow_run` trigger starts the `corpus-baseline`
preset on the amd64 box: the new master image alone, 20k requests at 100 rps, the 40-pass replay.
Bursts of pushes collapse to the newest *pending* run (concurrency group `rpc-bench-master-baseline`,
`cancel-in-progress: false`): GitHub keeps one running plus one queued, so an in-flight refresh always
finishes rather than being starved by the next merge — at the cost of up to two cells on the box. Two
things are kept:

- the staged aggregates (`summary.json`, `resources.json`, `timings.*`, per-class metrics) plus a
  `baseline.json` (image, date, run, cell fingerprint) go to the Actions cache under
  `rpc-corpus-baseline-<arch>-<corpus>-<cell>-<run id>`; a PR run restores the newest key with that prefix;
- the parity responses stay on the runner under `<expb data dir>/rpc-bench/baselines/<corpus>.json.gz`
  (response bytes never leave the box); a PR run compares against them (`corpus_baseline: use`).

The comment then names the master image, date and run the baseline came from. When no cache exists
yet (new arch, new corpus, changed cell shape, cache evicted after 7 idle days) the run falls back to
executing `nethermindeth/nethermind:master` itself; when the comparison against the saved responses
cannot run (snapshot head moved, unreadable state, node unreachable) the run **fails** with parity
"not checked" — with a saved baseline that check is the run's only correctness gate, so it is never a
warning — and the master baseline needs re-recording if the snapshot moved. Caches made from a feature
branch are visible to that branch only — production
baselines come from master. `tool_config.corpus_baseline` (`none|save|use`) is what the presets set.

`Validate the cached master baseline` runs before the sweep and drops a restored tree this run cannot
use — one staged at a different `corpus_results.BASELINE_SCHEMA` (a staged-file schema change since
that master push) or measured on another cell — with a `::warning::`; the sweep then measures master
in this job, so a schema bump costs a slower run rather than an empty comment.

Three limits worth knowing before trusting a cached comparison:

- **Only amd64 refreshes itself.** The `workflow_run` trigger takes the default `arch`, so every
  automatic baseline is an amd64 one. An `arch=arm64` corpus-ab therefore misses the cache and runs
  master in-job at roughly twice the runtime, every time. Record an arm64 one by hand after a master
  push — `-f benchmark_tool=corpus-baseline -f arch=arm64 -f docker_image=nethermindeth/nethermind:master-<sha7>`
  — or pass `baseline_image=<master image>` and take the two-arm run deliberately.
- **The two halves of a baseline live in different places.** The aggregates are in the Actions cache,
  which is repo-global and outlives any box; the parity responses are a file on one runner. So they can
  disagree: a box whose `baselines/` dir was cleaned still restores cached aggregates (the run then
  captures parity against its own arm and says so in the step summary), and the amd64 cache says
  nothing about what the arm box holds. The comment names the vintage of the aggregates; the step
  summary's parity table names the label of the saved responses. Read both.
- **The default cell is a warm-compute signal.** The 60 s warm-up at 400 rps delivers ~24k requests
  before a 20k-request cell, so both arms measure a node whose caches are hot — the analogue of expb's
  `EXPB_EVM_WARMUP=1`. That is the right default for compute changes and weak for storage-layer ones;
  for those pass `warmup=0` (cold, at the cost of ~2% failures and p99 reading ~60% high) or size a
  much longer cell.

`tool_config` templates (all keys optional; the inputs above already set the common ones):

```json
{"clients": "nethermind@<a> nethermind@<b> nethermind@<c>", "seed": 7, "corpus_passes": 40,
 "timings_rps": 100, "timings_concurrency": 16, "corpus_warmup_rps": 400,
 "parity_diffs": true, "max_corpus_records": 50000, "max_divergence_indexes": 500,
 "db_isolation_all": "copy", "node_env_vars": "NETHERMIND_EXPERIMENTAL_X=1", "resource_sampling": true,
 "benchmark_config": "config/benchmark/ethcallchaos-percategory-validated.yaml", "iso_configs": "", "iso_duration": "20s",
 "ref": "<json-bench sha>", "state_layout": "flat", "snapshot_block": "25490000", "corpus_dir": ""}
```
```json
{"mode": "benchmark", "benchmark_config": "ethcall-contracts-head", "vus": 10, "seed": 1, "deep_check": false,
 "compare_config": "config/compare/defaults.yaml", "concurrency": 5, "timeout": 30, "validate_schema": false,
 "fail_on_diff": false, "max_fail_rate_pct": 1, "html_report": true, "corpus_file": "", "extra_args": ""}
```
```json
{"tests": "eth_call eth_getBalance", "deep_check": false, "label": "", "extra_args": ""}
```
```json
{"parallel": 8, "leaderboard_top": 50, "api_port": 5000, "min_mean_ms": 1, "max_cv": 10, "corpus_db": "", "corpus_url": ""}
```

`node_config` template: see [`node_config` JSON](#node_config-json).

## Fixed corpus A/B against master

The canonical branch-vs-master check (`benchmark_tool: corpus-ab`, the default) is a fixed `eth_call`
corpus A/B — the 497-record corpus, 20k requests at 100 rps per arm, plus a closed-loop per-record
replay, `docker_image` against the cached master baseline (`baseline_image: cache`) or against a
given `baseline_image`. The inputs expand to this sweep config (with an explicit `baseline_image`,
`clients` lists both arms and `corpus_baseline` is `none`):

```json
{"eth_call_corpus": true,
 "clients": "nethermind@<docker_image>", "corpus_baseline": "use",
 "rps_list": "100", "corpus_requests": 20000, "rounds": 1, "timings_passes": 40,
 "corpus_warmup_duration": "60s", "corpus_glob": "eth-call-corpus-20260805T104605Z-497-safe.jsonl.gz"}
```

Pin both arms to prebuilt tags where you can: building the branch image on the benchmark runner
serializes every other job behind it. What makes the number trustworthy:

- **Request count, not wall time** (`corpus_requests`): every cell has the same sample size
  whatever the rate, so percentiles resolve equally at 50, 100 or 300 rps.
- **Seeded request sequence** (`seed`, default 1): json-bench draws corpus records with a fixed
  generator, so both arms replay byte-identical request sequences at every rate instead of two
  different random mixes of a heavy-tailed corpus.
- **Rounds** (`rounds`, default 1): with the frequency cap and seeded requests a single round lands
  within ~1–1.5% run to run, so one round is the default. `rounds: 2` runs the client list again in
  reverse (A B B A) — each node restarted and re-warmed, position bias cancelled, and the two master
  runs give an A/A control printed next to every delta — at twice the runtime; use it when a delta
  sits within a few percent.
- **The noise floor follows the evidence in the run.** A measured A/A spread (two master runs) sets
  it: twice the spread, minimum 1%. With no repeat it falls back to a constant — `NOISE_FLOOR_PCT`
  2.5% when both arms ran here, `CACHED_NOISE_FLOOR_PCT` 5% when master came from the cache, because
  then the arms are not co-run and the drift between the two jobs is measured nowhere. That ~1–1.5%
  figure and the 2.5% constant are *in-run* evidence, so the wider floor is a placeholder until the
  cross-job spread is measured from consecutive `corpus-baseline` runs; the comment says which floor
  it applied.
- **Paired per-record replay** (`timings_passes`, unpaced by default): each record is hit exactly
  N times per arm; the comment reports the median per-record delta with a bootstrap CI, how many
  records moved by more than 5%, and the closed-loop throughput at `timings_concurrency`
  (default 16) — the capacity signal an unsaturated 100 rps cell cannot show.
- **CPU-ms/request** from the node's cgroup, printed first: rate-independent and more sensitive to
  compute changes than p50 at an unsaturated rate. Its window brackets the json-bench container, so it
  also covers container start and k6's fixture parse, when the node is near idle: comparable between
  arms (same window shape), but not an absolute per-request cost.
- **Selector classes**: the corpus is split by 4-byte selector into `class_1..N` (ranked by record
  count) and the comment breaks p50/p99 down per class, so "p99 moved" becomes "class 2 moved".
- **CPU frequency cap** (`node_config.cpu_max_freq_khz`, default 3.8 GHz on amd64): turbo boost is
  off and the governor is `performance` for the whole job, restored afterwards.

The workflow's `Render corpus comparison` step runs `corpus_results.py comment --baseline
nethermind_<baseline tag> --candidate nethermind_<candidate tag>` against the **staged** tree
(labels derived from `baseline_image` / `docker_image` the way the sweep derives them, `_rN`
repeats pooled into one arm per side), appends it to the step summary and, on a
label-triggered PR run, posts it as a PR comment. Rendering is best-effort — the artifact is the
record — and a `tool_config.clients` override that names other arms is rendered by hand from
the downloaded artifact. Read it correctly: a parity divergence is a correctness regression
regardless of latency; a delta inside the A/A spread (or under the no-repeat floor the footer
names) is noise; an "Unequal load" line means the arms did not receive the same request count (k6
dropped iterations), so the deltas are not like for like.

## Private `eth_call` corpus (`tool_config.eth_call_corpus: true`)

For call sets that must not appear in GitHub logs or artifacts (e.g. shared by a
third party): the corpus lives only on the runner, and runs publish **aggregate
numbers and parity counts only**. This is a logging/artifact boundary, not a
defense against the runner itself — anything executing on the VM (trusted
images, this repo's scripts) can read the corpus there.

The boundary covers call *contents*, not the **filename**: everything after the
`eth-call-corpus-` prefix becomes the scenario label, which appears in the step
summary, the parity table, artifact paths, and `summaries.manifest`. That is
deliberate — scenarios have to be told apart — so name files by workload shape,
never after anything sensitive.

Two operational limits worth knowing before capturing. `corpus_parity.py` guards at
10,000 records by default — raise it deliberately with `max_corpus_records` when the
runner has the memory, since the replay holds every record's params at once. And the k6
fixture scales with record count (~142 MB for 497 records, since `eth_call` records with
state overrides run to hundreds of KB each), so the k6 cells are the binding constraint
on a large capture, not parity: prefer sampling down to a representative subset, or run
parity/timings only with an empty `rps_list`.

**Corpus files** (JSON Lines, one `{"method":"eth_call","params":[...]}` per
line, extra fields ignored, optionally gzipped) go to the runner at
`<expb data dir>/rpc-bench/eth-call-corpus[-<label>].jsonl.gz` — `/mnt/sda/expb-data` on the
amd64 runner, `/data/expb-data` on arm64, selected by the `arch` input. A
`jsonbench-sweep` with `eth_call_corpus:true` discovers **every**
`eth-call-corpus*.jsonl.gz` there and runs each as its own scenario;
single-node `jsonbench` uses the default `eth-call-corpus.jsonl.gz` only.
`corpus_dir` (sweep tool_config) overrides the directory.

**Sizing a cell by request count.** By default a corpus cell runs for `duration` at each
`rps_list` rate. `corpus_requests` (absolute) or `corpus_passes` (a multiple of that
corpus's record count) instead size the cell by how many requests it should issue: the
rate is unchanged and the length is derived as `ceil(count / rps)`, since k6's
constant-arrival-rate executor holds the rate. `corpus_passes: 5` on a 50k corpus at
`rps_list: "500"` is 250,000 requests over 500s. Draws are *with replacement* from a
seeded generator (`seed`, default 1): every arm and every rate replays the same sequence,
but coverage is still `N x (1 - (1 - 1/N)^requests)`, not a guarantee every record is visited.

**Selector classes.** `prepare-eth-call-corpus.py` splits the corpus by the 4-byte selector of
`params[0].data` into `class_1..N` fixtures (ranked by record count; records without calldata
form their own class) and renders one weighted json-bench call per class, so k6 emits a
per-class sub-metric. The published `summary.json` carries them under `metrics.classes`; the
selector-to-class mapping exists only in the fixture files on the runner.

**Per-record timings.** The k6 cells cannot attribute a latency to a corpus record. The sweep's
`timings_passes` replay (`corpus_parity.py timings`) walks the corpus in order N times per arm
— unpaced by default, i.e. a closed loop at `timings_concurrency` — and the PR comment pairs
the two arms record by record. To get the same matrix by hand against a running node:

```bash
python3 scripts/rpc-bench/corpus_parity.py timings \
  --corpus "/data/expb-data/rpc-bench/eth-call-corpus-<label>.jsonl.gz" \
  --rpc-url http://localhost:8545 \
  --out timings.csv --passes 5 --rps 100 --concurrency 16
```

Walks the corpus in order, `--passes` times, pacing submissions to `--rps` (0 = unpaced),
and writes one row per record with a duration **and an outcome** per pass:

```
record_index,pass_1_ms,pass_1_status,pass_2_ms,pass_2_status
1,6.855,ok,9.761,ok
2,37.086,ok,58.363,rpc_error:-32000
```

The status column exists because a rejected call returns early: without it a node shedding
load reads as a fast one. Exclude any measurement whose status is not `ok` before computing
percentiles — the run also prints a warning when any are present.

Every record is hit exactly `--passes` times — unlike the k6 cells, where coverage is a
random draw. The CSV carries record indexes, milliseconds and outcome names only, so it is
safe to publish under the same boundary as the parity reports.

A `timings.meta.json` sidecar records the head block hash, record/pass counts, target and
achieved rate, concurrency, and `warmup_seconds`/`warmup_rps` — the discarded warm load the
node absorbed before the matrix (0 seconds = measured cold). **Only compare matrices whose
metadata matches** — a different head, rate or concurrency makes the numbers incomparable, and
the warm-up fields most of all (the same seconds at a different rate is a different warm
state): a cold matrix reads ~60% higher on p99 than the same node warm, and nothing in the
CSV itself would reveal that. On k6-warmed runs the field is the exact
requested duration and can be matched literally; on replay-warmed runs it is a measured
elapsed value (and ~request+60 when the wall-clock bound fired), so compare it as
"both warm and within a few percent", not byte-for-byte.

**What a corpus sweep does per client:** first a discarded **warm-up, once per
corpus** (`corpus_warmup_duration`, integer seconds with an optional `s` suffix — `5m` is
rejected; default `60s`, `0` measures cold on purpose; an N-corpus sweep therefore burns
N x 60 s per client before measuring) driven at `corpus_warmup_rps` (default `400`, so the
window delivers the ~24k requests that `240s` at 100 rps used to, in a quarter of the wall
time; it is a floor, so a run measuring a higher rate warms at that rate instead, and
`corpus_warmup_rps_max` caps that pace — json-bench pre-generates rps x duration request rows
that k6 parses whole, so a long warm-up ahead of a high-rate cell would exceed what the fixture
can hold): a k6 cell
when `rps_list` is non-empty (seeded with `seed + 1000`, so a measured cell never replays exactly
the sequence the warm-up just ran), otherwise a paced `corpus_parity.py timings` replay so the
fixture-free mode stays fixture-free.

The request count is the point, and the rate is a request for one, not a guarantee of one — so
both branches record what they **delivered** and warn when it lands below 80% of the target.
Two reasons it can: `run_cell` does not scale `vus` with the rate, and k6's arrival-rate executor
drops iterations once demand outruns that pool; and 400 rps is above the 300 rps that already
measured a 1.22% fail rate on arm64, so on that box the warm-up is saturated by construction. The
warm-up's own fail-rate gate is lifted, so this warning is the only thing that reports it. The
replay branch is request-bounded rather than window-bounded for the same reason: its wall-clock
bound has a 300s floor, so a slow node still reaches the request target instead of being cut off
at the shorter window — meaning in that mode the warm-up can outlast `corpus_warmup_duration`.

Cold nodes fail ~2% of calls and read ~60% higher p99, so every measured number below assumes
this ran. Then one k6 latency cell per corpus per
`rps_list` entry (the corpus replaces the workload's `calls:`; rendered as a
JSON-array fixture because json-bench's JSONL reader caps lines at ~64 KiB),
then one full-corpus replay via `corpus_parity.py` while the node is still up.
The **first client in `clients` is the parity baseline** (or, with `corpus_baseline: use`, the
responses the last master-baseline run saved on the runner); every later client's
responses are compared byte-for-byte against it, and any defect or mismatch
fails the job. Calls the baseline client rejects with a JSON-RPC error are
recorded as error outcomes (captured corpora legitimately contain calls that
fail at the pinned head, e.g. explicit `gasPrice` with an underfunded sender);
both clients rejecting a call counts as agreement (`both_rpc_errors`), a
one-sided rejection as divergence. Corpus cells raise start-node's uniform
`RPC_GAS_CAP` from 1e9 to 1e12 so the corpus's explicit multi-billion `gas`
fields are not clamped into artificial failures. Images (`nethermind@image`), rates, and duration are all
free-form — pick rates the node can sustain, and mind that latency numbers from
a cold node at low rps are indicative, not steady-state.

**How contents stay off GitHub:** the json-bench container's output goes to a
VM-scratch file instead of the job log; per-call k6 outputs, deep-check, and
HTML reports are disabled or left in scratch; the published `summary.json` is
rewritten by `corpus_results.py sanitize` to a fixed numeric schema; parity
reports contain counters and client labels only; node logs are scanned for the
usual Exception / invalid-block / shutdown gates but print **counts only** and
are deleted (sweep) or excluded from upload; the artifact is assembled by
`corpus_results.py stage`, which copies nothing but files on its allowlist, each
behind an exact-schema validator (`jsonbench-summary.md` is the one exception — it
is generated strictly downstream of the sanitized schema; `summaries.manifest` is
validated line-by-line with its paths rewritten artifact-relative — and being an index of
files that are themselves validated, a malformed manifest drops only itself with a warning
rather than failing the artifact): `summary.json`, `parity.json`, `timings.csv`
(indexes, milliseconds and outcome names), `timings.meta.json` (block identity and
run parameters, including `warmup_seconds`), `resources.json` (cgroup counters),
`parity-diffs.json`, and the generated markdown/manifest. `parity-diffs.json` is
the one artifact derived from response bytes: **opt-in** (`parity_diffs`, default
off) and reduced to word positions plus a higher/lower direction — no operands, no
magnitudes, enforced by its validator. Failures print category + counts (e.g.
`rpc_error=3`), never request or response bytes — raw detail stays on the
runner in `<scratch>/jsonbench/` for SSH diagnosis until the next run wipes it.

Example — 4-way private comparison of Nethermind builds, 3 rates, both corpora,
one dispatch:

```json
{"eth_call_corpus": true,
 "clients": "nethermind@nethermindeth/nethermind:master nethermind@nethermindeth/nethermind:some-pr-branch nethermind@nethermindeth/nethermind:paprika nethermind@nethermindeth/nethermind:performance",
 "rps_list": "50 100 300", "corpus_requests": 20000, "rounds": 2}
```

## dotTrace flow (goal #3)

Setting `dottrace` to a mode (requires `client=nethermind`) uses the same mechanism as
expb's `--dottrace`, so it works with **any** Nethermind image (no special diag build).
Reports captured before the `DOTNET_TieredCompilation=0` pin removal are not
comparable with newer ones (tiering shifts OwnTime/TotalTime attribution).

| mode | what it measures | XML report | cost |
| --- | --- | --- | --- |
| `sampling` | periodic stack samples; wall-clock attribution | yes | low — the default for hot-spot work |
| `tracing` | every method enter/exit; exact call counts | yes | high (~4x) — read counts, not times |
| `timeline` | samples plus thread states over time (waits, locks, GC) | **no** | moderate — open the snapshot in the dotTrace UI |

Line-by-line is not offered: it needs PDBs the Docker images do not ship.

1. The host-installed dotTrace CLI (`/opt/dottrace`, installed on demand via
   `dotnet tool install JetBrains.dotTrace.GlobalTools`) is mounted read-only
   into the container, and the entrypoint is wrapped:
   `dottrace start --framework=NetCore --profiling-type=<Mode> --save-to=... --propagate-exit-code -- /nethermind/nethermind …`.
2. `stop-node.sh` stops the container with **SIGINT**, letting dotTrace finalize
   the snapshot into the mounted diag dir (`.dtp`, or `.dtt` for `timeline`).
3. The `generate-dottrace-reports` job (Windows) runs `Reporter.exe` to convert
   each `.dtp` to XML — identical to the EXPB workflow. It is skipped for
   `timeline`, whose snapshots Reporter.exe cannot post-process.
4. The `dottrace-summary` job runs [`scripts/dottrace-report.sh`](../dottrace-report.sh)
   `top` over each XML and writes the hot functions into the job summary.

The EXPB workflow additionally collects a **dotnet-trace EventPipe sidecar** alongside
its dotTrace snapshot (GC pauses, lock contention, exception throws — runtime events a
CPU profile cannot show). rpc-bench does not: a `timeline` run here yields the `.dtt`
snapshot only, for the dotTrace UI.

> The snapshot spans the node's whole lifetime (including DB load and warmup), so
> keep the benchmark `duration` the dominant phase, or analyze by time window, so
> RPC-call frames dominate the captured `OwnTime`.

Download the `dottrace-reports` artifact and inspect locally:

```bash
scripts/dottrace-report.sh top   reports/<name>-report.xml 30
scripts/dottrace-report.sh compare reports/before.xml reports/after.xml 30
```

## Runner prerequisites

The `reproducible-benchmarks-arm` self-hosted runner must provide:

- **Docker** (nodes run as containers; EthCallChaos runs in a .NET SDK container;
  json-bench builds and runs its own runner image).
- **The block-tagged Nethermind flat snapshot sets** shared with expb
  (`<snapshot root>/nethermind-flat-<block>`, e.g. `nethermind-flat-25490000`).
  A client or layout with no set there is refused up front rather than run.
- **A writable scratch location** on the same large disk (default
  `<expb data dir>/rpc-bench-scratch`).
- **`mount`/`umount` privileges** and overlayfs (expb already uses both).
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
| `corpus_parity.py` | Private corpus replay: capture a baseline client's responses (VM-local), diff later clients against it, emit counts-only reports. |
| `corpus_results.py` | Sanitize k6 summaries to a fixed numeric schema, stage only validated aggregate files, render the PR comment (pooled repeats, A/A spread, paired timings, classes). |
| `prepare-eth-call-corpus.py` | Split a JSONL(.gz) corpus into per-selector-class JSON-array fixtures for json-bench. |
| `run-jsonbench.sh` | Clone/build json-bench's runner image, render the workload config for the node(s), run `benchmark` (summary.json metrics, no Prometheus) or `compare`, report. |
| `run-rpc-sweep.sh` | One node per `clients` entry `ctype[@image][#K=V[,K=V]]` (times `rounds`, ABBA; the env suffix is passed as `docker -e` to that arm's node only and folded into its label, so one sweep can compare config values of one image), cells per rps, corpus warm-up/parity/timings, step summary. |
| `cpu-stabilize.sh` | Turbo off, `performance` governor, optional `scaling_max_freq` cap for the job; restores the originals afterwards. |
| `sample-resources.py` | Per-cell cgroup counters for the node container (CPU-ms/request, IO, PSI). |
| `percat-matrix.py`, `deep-check-compare.py` | Sweep step-summary tables; cross-client response diff of deep-check captures. |
| `cleanup.sh` | Guarded defensive cleanup (stale containers, leftover mounts, scratch). |
