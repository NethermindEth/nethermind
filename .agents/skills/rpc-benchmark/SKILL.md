---
name: rpc-benchmark
description: Dispatch and interpret the JSON-RPC benchmark workflow (run-rpc-benchmarks.yml) - runners, eth_call corpora, usable request rates, and A/B sweeps. Use when benchmarking eth_call, trace_*, or debug_* RPC performance.
---

# RPC Benchmark Workflow Guidance

- Workflow file: [`.github/workflows/run-rpc-benchmarks.yml`](../../../.github/workflows/run-rpc-benchmarks.yml)
- Scripts and full reference: [`scripts/rpc-bench/README.md`](../../../scripts/rpc-bench/README.md)
- [Linux perf flow](../../../scripts/rpc-bench/README.md#linux-perf-flow) documents the root-only RPC capture contract; [`scripts/perf-report.sh`](../../../scripts/perf-report.sh) reads folded profiles from both EXPB and rpc-bench.

`run-rpc-benchmarks` measures state-reading JSON-RPC (`eth_call`, `eth_getBalance`, `trace_*`,
`debug_*`) against a parked DB snapshot on the same two benchmark runners as expb — pick the box with
`arch`, and pass `docker_image` explicitly so the runner pulls a prebuilt tag rather than building one.
The default preset (`benchmark_tool=corpus-ab`) compares `docker_image` against the **cached master baseline**:
after every master push that changes `src/Nethermind/**`, `Publish Docker image` completes and a `workflow_run`
trigger records `nethermind:master-<sha7>` alone on the corpus (`corpus-baseline` preset) — its aggregates go to the
GitHub Actions cache (`rpc-corpus-baseline-<arch>-<corpus>-<cell>-<run id>`, newest wins), its parity responses stay
on the runner under `<expb data dir>/rpc-bench/baselines/`. A PR run therefore executes only the PR image and the
comment names the master image, date and run the baseline came from; with no cache yet it runs `nethermind:master`
itself. `<cell>` is a hash of every knob that shapes the cell (request count, warm-up, seed, replay passes, rps,
`node_env_vars`, cpu cap/cpuset/memory, …), so changing any of them misses the cache on purpose and the run measures
master in-job — never compare a cached baseline across cell shapes. Only **amd64** baselines refresh automatically
(the `workflow_run` path takes the default `arch`), so an `arch=arm64` corpus-ab always takes that two-arm fallback at
~2x the runtime unless you record an arm64 baseline by hand (`-f benchmark_tool=corpus-baseline -f arch=arm64`).
Cached aggregates and on-runner parity responses are separate state and can come from different master vintages.
`baseline_image=<image>` forces a real two-arm A/B in one job; `rounds=2` (A B B A) adds an in-run A/A control at
twice the cost — the frequency cap and seeded requests make single rounds land within ~1–1.5%, so 1 is the default.
More than two arms: `tool_config.clients` (`nethermind@<image>` per arm) overrides the derived list.

## What the runners actually hold

Both boxes carry **one** private `eth_call` corpus, `eth-call-corpus-20260805T104605Z-497-safe.jsonl.gz`
= **497 records** (heavy simulation traffic: every record carries state overrides, median ~331 KiB). The
sweep discovers it by glob and prints `Corpus scenarios: …` / `corpus OK: 497 records` — read those lines
rather than assuming a corpus set. Pin one with `corpus_glob` when more are added.

The canonical cell is **20,000 requests at 100 rps per arm after a discarded 60 s warm-up at 400 rps**, plus a
40-pass closed-loop per-record replay (`timings_passes: 40`); the request sequence is seeded so every arm replays
identical requests, the CPU frequency is capped for the job, and the PR image is compared against the cached master
baseline (see `scripts/rpc-bench/README.md`, "Triggering" and "Fixed corpus A/B"). Rates are
the thing to get right:

| rate | usable? |
|---|---|
| 10 | **no** — 300 requests gives mean CV ~70%, p99 CV ~206%; one cold outlier dominates |
| 50–100 | yes; CV ~1–3% on mean/p50, p99 needs n>=3 |
| 300 | amd64 only — on arm64 it drove a **1.22% HTTP fail rate**, tripping the 1% gate, after which percentiles above p98 describe failures, not latency |

Size a cell by request count instead of duration with `corpus_requests` (absolute) or `corpus_passes`
(a multiple of the corpus's record count) — `corpus_passes: 5` on 497 records at 100 rps is ~2,485
requests. Note these are draws *with replacement*, so coverage is `N x (1 - (1 - 1/N)^requests)`, not a
full pass. `corpus_parity.py` refuses corpora above **10,000 records** unless `max_corpus_records` is
raised, and the k6 fixture is the real ceiling long before parity is (~142 MB for 497 records), so a
50k-record capture wants sampling down rather than a bigger cap.

For reference, expb's sweeps on the same boxes are sized by `amount`: `superblocks` defaults to 100,
`realblocks` and `fusaka` to 1000, and both of the latter have 10k payloads available (fusaka covers
blocks 25,490,001-25,499,999). Separately, `benchmark_tool=ethcallchaos` uses the EthCallChaos SQLite
corpus (`corpus-v2`, ~1.1 GB) rather than these JSONL corpora, and with a seeded corpus it re-reports
its own stale timings — use the json-bench per-category config for an A/B instead.

```bash
# default preset corpus-ab: docker_image (the PR build) vs the cached master baseline on the private corpus,
# 20k requests at 100 rps, 40-pass replay; every knob is a plain input, JSON is only for overrides
gh workflow run run-rpc-benchmarks.yml --ref <branch> \
  -f arch=amd64 -f docker_image=nethermindeth/nethermind:<pr-tag> -f baseline_image=nethermindeth/nethermind:master-<sha>
```
