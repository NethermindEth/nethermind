---
name: expb-benchmark
description: Dispatch, profile, and interpret the expb reproducible payload benchmark workflow (run-expb-reproducible-benchmarks.yml) - inputs, runners, dotTrace/perf/nettrace profiling, log structure, and mandatory log checks. Use when running or analyzing an expb benchmark run or its profiling artifacts.
---

# Reproducible Benchmark Workflow Guidance

This repository contains a dedicated workflow for reproducible payload benchmarks:

- Workflow file: [`.github/workflows/run-expb-reproducible-benchmarks.yml`](../../../.github/workflows/run-expb-reproducible-benchmarks.yml)
- Execution runner: chosen by the `arch` input — `amd64` (default) runs on `reproducible-benchmarks`
  with snapshots under `/mnt/sda`; `arm64` runs on `reproducible-benchmarks-arm` with snapshots under
  `/data`. ARM requires flat layout; it supports Nethermind and the Reth Fusaka snapshot at
  `/data/reth/reth-25490000`, while Geth requires amd64. Reference-client runs are
  workflow-dispatch Fusaka runs with explicit `docker_images` and `state_layout=flat`; the workflow
  rejects Nethermind-only flags and environment settings. **Never compare timings across the two boxes.**

## What the workflow does

- Resolves runtime inputs (branch, state layout, payload set, delay, optional extra flags).
- Selects one benchmark config file from the runner's expb data dir (`/mnt/sda/expb-data` on amd64, `/data/expb-data` on arm64).
- Builds or reuses Nethermind Docker image tag depending on branch rules.
- Renders a temporary config (does not modify source files) by:
  - replacing `<<DOCKER_TAG>>`
  - replacing `<<DELAY>>`
  - renaming scenario key `nethermind:` to a detailed scenario name
  - appending user-provided extra flags under `extra_flags:`
- Installs `expb` via `uv tool install --force --from ... expb`.
- Runs `expb execute-scenarios` with per-payload metrics and logs.
- Handles termination gracefully with cleanup grace period.
- Metrics: one table per payload set with three column groups, each as Master, PR and delta. "Request (k6)" is the per-payload newPayload request time from expb's pipe table (k6's time to first byte today), the figure the consensus client waits for. "Processing" is the client's own block processing time from the SSE data feed (`[payload-server] client_metric` lines); use it for EVM and state changes. "Request - processing" is the per-payload difference, the request path and GC. A change that moves time between windows shows as opposite deltas in the first two groups and a matching move in the third. The MGas/s row carries a figure for the first two groups: the same gas total over that group's own time, across the same payloads. Read the request figure as the end-to-end throughput a consensus client sees and the processing figure for EVM and state work; the third group is a residue rather than a window gas is delivered in, so it has no throughput. Columns without a comparable baseline read n/a; without SSE data only the request group has values.
- `measurement_source=auto` uses SSE when available; `engine-api` skips SSE and uses K6 request timing, and is forced for other clients.
- On successful `master` push runs, caches timing aggregates (AVG/MEDIAN/P90-P99/MIN/MAX). On PR runs, posts a comparison comment.
- The `single-summary` job aggregates across runs and payload sets into `GITHUB_STEP_SUMMARY` (per-run table + mean/best/worst when `run_count > 1`).
- The `dottrace` input selects a profiling mode — `false` (default), `sampling`, `tracing`, or `timeline` (`true` is a legacy alias for `sampling`) — and passes `--dottrace --dottrace-mode <mode>` to expb. Pick by question: `sampling` for "where does time go" (low overhead, the default choice), `tracing` for exact **call counts** (~4x overhead, so read its counts and distrust its times), `timeline` for waits/locks/GC over time. dotTrace snapshots (`.dtp` + chunk files; `.dtt` for timeline) are zipped and uploaded as artifacts.
- A downstream Windows job (`generate-dottrace-reports`) runs Reporter.exe to produce XML reports (`*-report.xml`) uploaded as the `dottrace-reports` artifact. Each report contains `<Function>` nodes with `FQN`, `TotalTime`, `OwnTime`, `Calls`, and full call stacks — sort by `OwnTime` for hot spots, use `CallStack` attributes for call tree analysis. **`timeline` produces no XML** (Reporter.exe cannot convert it) — that job is gated off, so analyze the snapshot in the dotTrace UI instead.
- Every profiled **EXPB** run also collects a **dotnet-trace EventPipe sidecar** (`.nettrace`, in the same `dottrace-*` artifact, or `profiling-*` when perf is enabled; rpc-bench collects one too with `dotnet_trace=true`, as the `dotnet-trace-rpcbench` artifact, covering the measured phase only — the collector attaches after the warm-up, so that input is accepted for a single-node `jsonbench` run and supplies a 60s `tool_config.corpus_warmup_duration` when the dispatch sets none). It carries GC pause durations, lock contention, and exception events, which no CPU profile shows — use it whenever the question is about tail latency or stalls rather than hot code, and note it is the only structured output for `timeline` runs. Summarize one with [`scripts/nettrace-report.cs`](../../../scripts/nettrace-report.cs) (`dotnet run scripts/nettrace-report.cs -- <file.nettrace>`): GC pauses per generation, contention percentiles, exception count. Its contention figure is blocked time only — a sampling profiler books pre-block spinning against `Monitor.Enter_Slowpath`, so disagreement between the two is expected and informative.
- Linux perf profile: pass `perf=true` to sample the client with `perf` on the host. This is the only way to see inside dotTrace's `[Native or optimized code]` node, which is routinely the third-largest entry in a snapshot: perf resolves managed frames from the runtime's perf map and native frames from the container's shared objects, so RocksDB, the allocator, `memset`/`memcpy` and GC time are attributed individually. The artifact carries `perf.folded` (one line per unique stack); raw `perf.data` is excluded. `perf` and `dottrace` are independent inputs; enabling both samples the process twice, so use perf runs for attribution and keep A/B timing numbers to dottrace-only or unprofiled runs.
  - Symbolization is partial and worth checking first: on a verified run 19% of samples resolved to
    managed frames, 55% to native ones (snappy, secp256k1, LZ4, kernel) and 26% stayed `[unknown]`,
    almost all of it inside the stripped `libcoreclr.so` and `librocksdb.so` shipped in the image.
    perf therefore narrows dotTrace's single opaque node to a named library plus a resolved majority,
    but it does not eliminate it - an unstripped build would be needed for the rest.
  - The capture covers every thread of the client process, RocksDB's background compaction pool included - on a short run that pool was 38% of process CPU and nearly half of it was snappy. Split by the leading `comm` field before attributing anything to block processing: `awk -F';' '$1==".NET"'` keeps the runtime's threads, `$1=="rocksdb:low"` the compaction ones.
  - perf samples CPU cycles, so idle threads are absent and the percentages are shares of CPU, not of wall clock. They are not comparable with dotTrace's wall-clock percentages.
- Targeted per-block dotTrace: pass `trace_blocks=<n1,n2,...>` (implies `dottrace=true`); the client's BlockProfiler plugin brackets each listed block. The artifact is one `.dtp` workspace with **one snapshot per traced block** (open in the dotTrace UI; `.dtp.NNNN` files are storage segments, not per-block files). The XML report merges all traced windows, so trace a single block per run when isolated XML matters.

## What to inspect in run output

- Inspect the `Run expb scenarios` step output first.
- Treat any Nethermind `Exception` as a high-priority issue.
- Explicitly scan logs for invalid block signals, including `Invalid Block` and `Invalid Blocks`.
- Review the end-of-run summary section with per-block timings and totals.
- Use summary timing values to derive aggregate metrics (average/mean at minimum; median/p95 when available).
- If a run fails or is terminated, check whether cleanup grace-period handling completed cleanly.

## Log structure reference

- Reference run used for structure validation:
  - Run: `https://github.com/NethermindEth/nethermind/actions/runs/22185801008`
  - Job: `https://github.com/NethermindEth/nethermind/actions/runs/22185801008/job/64159725161`
- Fetch logs with:
  ```bash
  gh run view 22185801008 --job 64159725161 --log
  ```
- GitHub job log lines are tab-separated in this shape:
  - `<job-name>\t<step-name>\t<timestamp>\t<message>`
  - Example step names in this workflow: `Print resolved inputs`, `Render benchmark config`, `Install or upgrade expb`, `Run expb scenarios`.
- `Run expb scenarios` contains mixed streams:
  - EXPB structured events like: `timestamp=... level=info event="..."`.
  - K6 progress and metric blocks (`http_req_duration`, `iteration_duration`, percentiles like `p(95)`).
  - Raw Nethermind runtime logs (received blocks, processed block timings, shutdown sequence).
  - Per-payload metrics table near the end, marked by:
    - `+---------+------------+-----------------+`
    - `| payload | gas_used   | processing_ms   |`
    - rows with payload id, gas used, processing time.
- ANSI color codes are present; when searching/parsing, strip ANSI escape sequences first.
- Some non-ASCII time-unit glyphs can appear mangled in plain terminal output, so prefer numeric metric fields when computing aggregates.

## Mandatory log checks

- Fail review if any of these appear in Nethermind logs:
  - `Exception`
  - `Invalid Block`
  - `Invalid Blocks`
- Workflow behavior requirement: any detected `Exception` in run output must fail the workflow after reporting matching lines.
- Also flag severe runtime signals if present:
  - `Unhandled`
  - `Fatal`
  - `ERROR`
- Confirm normal shutdown markers at end:
  - `Nethermind is shut down`
  - `event="Cleanup completed"`

## Notes for agents

- The benchmark config is rendered to a temporary file and removed afterward; no source config revert is required.
- For `pull_request` and `push` auto-runs, default mode is `flat` layout with both `superblocks` and `realblocks` payload sets.
- Keep benchmark-related changes isolated to the workflow and benchmark guidance unless explicitly asked otherwise.
- Optional low-variance mode: pass `-f expb_env="EXPB_EVM_WARMUP=1"` to enable expb's per-block EVM warmup (`eth_simulateV1` before each measured block). It serves the measured block's reads from warm caches, which lowers both run-to-run CV (~1.8%→~0.55% on flat-realblocks) and AVG. Pair it with a raised RPC gas cap — `-f additional_extra_flags="--JsonRpc.GasCap=1000000000000"` — otherwise the per-request gas budget (default 100M) is exhausted on dense blocks and the warmup `eth_simulateV1` calls fail with `-38013` (intrinsic gas), silently leaving those blocks un-warmed. Caveat: warmup minimizes cold RocksDB/storage interaction, so it is a low-variance *compute* signal, not a substitute for the default cold benchmark — don't use it when measuring storage-layer changes.
- perf profiles are folded stacks, one line per unique stack. Use [`scripts/perf-report.sh`](../../../scripts/perf-report.sh): `top <perf.folded> [N]` for self time, `total` for inclusive time, `native` to list only unmanaged frames, and `compare <a.folded> <b.folded> [N]` for shifts between two profiles. Counts are reported as a share of the profile so runs of different length stay comparable. Pure awk, seconds even on large profiles.
- dotTrace XML reports are 50-70MB. **Never load full XML into context.** Use [`scripts/dottrace-report.sh`](../../../scripts/dottrace-report.sh): `top <report.xml> [N]` for hot spots, `compare <a.xml> <b.xml> [N]` for regressions/improvements. Runs in <2 seconds via grep+awk.
