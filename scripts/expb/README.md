# EXPB regression measurements

Use two separately labelled measurements. A lower coefficient of variation (CV)
is useful only if the benchmark still measures the behavior being changed.

| Mode | Purpose | Interpretation |
| --- | --- | --- |
| Standard | End-to-end block processing against the existing snapshot | Includes the configured storage, caches, persistence and normal GC behavior. A fresh overlay does not itself imply cold OS caches. |
| Compute warm | Fast screening of EVM and state/trie computation | Runs `eth_simulateV1` before each measured payload. This warms some code/state/database reads; it does not replace RocksDB or eliminate all storage costs. |

Never compare timings between modes, architectures, payload sets, snapshot
versions or different metric sources. Keep the ordinary standard run as a
confirmation gate for compute changes and as the primary test for storage changes.

Check warmup compatibility with the client being tested. The 2026-09-13
calibration found repeated `eth_simulateV1` failures on `master-d7bd8d0`
(`Cannot move unknown block ... to main`); that run cannot establish warmed CV.
Use standard mode until a client passes the complete warmup and health checks.

## State-root interpretation

Warm reads do not remove the requirement to compute and validate the imported
block's state root. They can nevertheless hide regressions in node loading,
decoding, cache misses, database lookup counts, working-set size and contention
with background persistence. Warmup also changes JIT, allocation and GC history.
The measured root calculation is therefore a warm-path result, not a prediction
of root calculation against cold state.

Replacing RocksDB with an in-memory database is a further change in workload:
it removes native reads, compression, write/flush/compaction behavior and their
interaction with managed work. Use such a harness for targeted algorithm tests,
not as the sole state-root regression benchmark. The compute-warm EXPB mode keeps
the real database and provides a less invasive screening measurement.

## GC investigation

Do not describe `--Merge.SweepMemory=NoGC` as disabling GC. It disables the
collections requested by Nethermind's post-block sweep strategy; .NET may still
collect. `PrioritizeBlockLatency` already enables attempts to enter a bounded
no-GC region, subject to sync mode and the available allocation budget. A long
campaign cannot assume those attempts always succeed.

First measure pause counts and durations using a separate diagnostic run and
`dotnet run --file scripts/nettrace-report.cs -- <capture.nettrace>`. Retain an unprofiled baseline: profiling changes
execution and must not be mixed into the timing comparison. The existing
dotTrace modes collect an EventPipe sidecar; `timeline` has no XML report.

Compare the normal settings with `--Merge.SweepMemory=NoGC` only as a labelled
experiment. Inspect total elapsed time, client processing time, K6 TTFB, GC pauses
and memory pressure together. Moving GC outside the measured block can improve
latency while leaving total throughput unchanged or worse. A cheap RPC before a
payload is not a GC-completion barrier. Do not subtract pauses or silently discard
GC-affected samples from the primary result.

Heap sizing and latency settings are experimental factors, not default fixes.
Larger managed heaps can reduce collection frequency while competing with
RocksDB and the OS page cache. Low-latency GC modes do not promise zero collections.
See the [.NET latency documentation](https://learn.microsoft.com/en-us/dotnet/standard/garbage-collection/latency)
and [GC configuration reference](https://learn.microsoft.com/en-us/dotnet/core/runtime-config/garbage-collector).

## Calibration and regression search

1. Pin the EXPB revision and a prebuilt multi-architecture image digest. Fix the
   snapshot, payload count/order, client flags, CPU allocation and delay. Calibrate
   amd64 and arm64 independently; never average the two machines.
2. Run the same image three times in standard mode and three times in compute-warm
   mode as an initial calibration. This estimates noise; it is not evidence of an
   optimization. Repeat in reverse mode order before attributing a small difference
   to a setting. The previously documented ~0.55% warm CV is historical evidence,
   not a guarantee for this image, machine or payload set.
3. Calculate across-run CV of the same aggregate: `100 * sample_stdev(run_means) /
   mean(run_means)`. With one run report CV as unavailable. Variation between
   different blocks inside a run is workload heterogeneity, not reproducibility.
4. Screen a long commit list with one pass per image. Treat this as candidate
   detection. Repeat only suspicious commits and their neighbors, in both orders,
   against a nearby baseline. Insert repeated baseline images in long campaigns
   to expose machine drift. Predefine the confirmation sample count and practical
   regression threshold; do not repeatedly test until a desired result appears.
5. Confirm compute candidates in standard mode. For storage, cache, persistence,
   allocation or GC changes, standard mode remains required even when warm
   results look unchanged. Use a write-heavy payload set as well as real blocks
   when state-root or persistence changes warrant it.

Preserve block IDs and compare matching payloads. Reject incomplete delivery,
failed warmup and invalid blocks. Do not pool SSE and TTFB as though they measure
the same interval. Per-block pairs help explain a change but are not independent
machine repetitions; contiguous blocks share state and cache history.

For orientation, if two independent run means each have a *known* CV of 0.5%, the
normal-approximation 95% noise width for their difference is about
`1.96 * sqrt(2) * 0.5% = 1.39%`. Estimating CV from only three observations adds
substantial uncertainty. One fast run cannot reliably establish a 0.2% improvement.

## Campaign lifecycle and artifacts

An explicit `docker_images` list or `enable_retrospective=true` selects the
sequential campaign. `run_count` repeats each image inside that same benchmark
job. The automatic branch/PR path retains its existing payload-set jobs.

For example, screen the last 50 published master images on one architecture:

```bash
gh workflow run run-expb-reproducible-benchmarks.yml --ref <workflow-branch> \
  -f arch=amd64 -f state_layout=flat -f payload_set=fusaka \
  -f enable_retrospective=true -f retrospective_last=50 -f retrospective_step=1 \
  -f run_count=1 -f measurement_mode=standard -f amount=1000 \
  -f expb_env='EXPB_SKIP_OVERRIDE=11,EXPB_WARMUP_OVERRIDE=0'
```

Retrospective mode selects published images, which need not represent every
commit. For an exact sequence, pass comma-separated full references in
`docker_images`. Repeating a reference is supported and retains a distinct
position in the campaign. Use immutable digests for calibration. Replace
`arch=amd64` with `arch=arm64` for a separate campaign on that runner.

Set up the runner and install EXPB once, then execute images sequentially. Every
sample must start a fresh client on a fresh writable snapshot view. Wait for the
client to stop before reverting the overlay. EXPB's cleanup-completed message is
not sufficient by itself: upstream overlay cleanup can swallow unmount errors,
so verify that the campaign's mounts and containers are gone before continuing.

Keep one directory per sample with a unique ID, UTC timestamps, full combined
stdout/stderr, resolved configuration, image identity, metric source, per-block
timings, quality status and elapsed time. Upload failed samples too. Profiling
artifacts remain separate from ordinary timing samples. A hard runner loss can
still prevent GitHub's artifact upload; a local log is not a durable off-runner
checkpoint until uploaded.

The `expb-campaign-<run-id>` artifact contains `campaign.json`, `summary.md`,
and a directory for each image/repetition. Each sample includes the timestamped
combined log, cleaned log, rendered config, `metrics.env`, `metadata.json`,
and matching exception/invalid-block/severe-signal lines. Campaign files live on
the benchmark data volume under `campaigns/<run-id>/<attempt>`. Console lines
are capped at 4,096 characters; artifacts preserve the full lines. Preflight
requires 2 GiB free on the root filesystem and 10 GiB on the data volume.
A failed or incomplete compute warmup skips further repetitions of that image
after verified cleanup, while retaining the failure in the campaign result.
Single-mode runs
also upload a logs artifact for each payload set and repetition.

For the current Fusaka corpus, the runner configuration replays eleven entries
through snapshot block 25,490,000 as warmup. ARM does not retain state for the
older entries, causing transaction-pool exceptions during that replay. A
calibration can explicitly set
`expb_env=EXPB_SKIP_OVERRIDE=11,EXPB_WARMUP_OVERRIDE=0` with an EXPB revision that
supports the skip override. Verify that the measured range remains
25,490,001–25,491,000 for `amount=1000`. This changes startup warmup history and
must be recorded; do not silently apply it to other corpora or snapshots.

Batching removes repeated checkout/install/job handoff overhead, but it does not
remove necessary teardown. In the existing amd64 Fusaka run
[34724607883](https://github.com/NethermindEth/nethermind/actions/runs/34724607883),
sample 1 spent about 66 seconds from executor setup to workload completion and
62 seconds stopping the client. The run then completed cleanup normally. Measure
setup, workload and shutdown separately before deciding where further speed work
belongs. Shortening the Docker stop timeout would change clean shutdown into a
potential forced kill; it is not a substitute for verified snapshot isolation.

## Rollout acceptance

See [the 2026-09-13 calibration](CALIBRATION-20260913.md) for measured results
and excluded diagnostic runs.

- Local tests cover ordering, repeated/colliding image tags, failed execution,
  missing metrics, warmup failure and leftover snapshot/container detection.
- One sequential campaign runs on each architecture and emits usable artifacts.
- Same-image calibration reports CV with its sample count and metric source.
- All accepted samples have no exceptions or invalid blocks, normal Nethermind
  shutdown and verified cleanup. Failures remain visible in the campaign result.
- Report measured campaign duration and setup/teardown contributions. Do not
  claim a CV or speed improvement from workflow changes alone.
