# Linux priority experiment

This is a small, opt-in experiment for checking whether Nethermind's block-processing priority handling changes on Linux. The EXPB branch passes an explicit mode into the Nethermind container:

- `observe`: request the existing managed `Highest` priority and record the effective Linux policy and nice value without changing the nice value.
- `reth`: request the same managed priority and apply the current Reth-style Linux nice policy to the processing thread: try `-20`, then fall back to `-6` when the stronger request is denied, restoring the original value when the scope ends.
- `nice`: the historical prototype arm, which temporarily sets nice to `-5`; its old timing results do not measure the Reth policy.

All enabled modes receive Docker's `SYS_NICE` capability. An explicit `off` is forwarded to Nethermind without that capability; an absent EXPB mode leaves the container arguments unchanged. The Nethermind revision in this PR defaults to the Reth-style best-effort mode on Linux, so a Reth comparison must use explicit `observe` as its control arm. A historical image tag does not contain the `reth` mode; build and publish an image from the Nethermind revision in this PR before benchmarking it. These probes are scoped to the synchronous processing thread; they do not claim to reproduce Reth's whole scheduler or boost every trie worker. Restoration is intentional because .NET thread nice values belong to native threads and must not leak through pooled-thread reuse.

The Reth reference is pinned to commit [`95823365b9f0787a676de38c044b54830e3fb29d`](https://github.com/paradigmxyz/reth/tree/95823365b9f0787a676de38c044b54830e3fb29d), including [`crates/tasks/src/utils.rs`](https://github.com/paradigmxyz/reth/blob/95823365b9f0787a676de38c044b54830e3fb29d/crates/tasks/src/utils.rs) and its engine call paths under [`crates/engine`](https://github.com/paradigmxyz/reth/tree/95823365b9f0787a676de38c044b54830e3fb29d/crates/engine). In that revision, `once!` owns a static `Once` per call site and is used on the sparse-trie blocking task, so it is once per call site rather than once per worker thread. The `Crossplatform(62)` conversion is pinned to [`thread-priority` 3.1.1](https://docs.rs/thread-priority/3.1.1/src/thread_priority/unix.rs.html): `floor(39 * (1 - 62 / 99) - 20) = -6`. Nethermind's probe applies the same normal Linux scheduling intent around each actual native processing thread, then restores the state for safe .NET pooled-thread reuse. Its scope is a `ref struct`, so C# prevents it from crossing an `await`.

## A/B dispatch

Before starting, publish the Nethermind `perf/linux-block-priority-review` branch (which contains the workflow and verifier), the EXPB `perf/linux-priority-review` branch, and a Nethermind prototype image containing the runtime change with both amd64 and arm64 manifests. Use a unique commit-based image tag for every arm and check that its registry digest is unchanged before and after the runs; the current workflow renderer requires tag references in `docker_images` and reconstructs the image from the tag. The workflow's `docker_images` input selects explicit images and `rebuild_docker=false` prevents a build. Keep `state_layout`, `payload_set`, `amount`, `delay_seconds`, `additional_extra_flags`, `flat_write_buffer_floor`, `run_count`, and profiling inputs identical.

Run an ABBA sequence independently on each architecture: observe (A), reth (B), reth (B), observe (A). Wait for each dispatch to finish before starting the next one because the benchmark runner is shared. Use fresh workflow dispatches, keeping the image and all other inputs unchanged. For example, replace `IMAGE` with the same prebuilt image reference in all four commands:

```bash
gh workflow run run-expb-reproducible-benchmarks.yml --ref perf/linux-block-priority-review \
  -f arch=amd64 -f expb_branch=perf/linux-priority-review \
  -f state_layout=flat -f payload_set=realblocks -f amount=1000 \
  -f docker_images=IMAGE -f rebuild_docker=false \
  -f expb_env='EXPB_NETHERMIND_PRIORITY_MODE=observe'

gh workflow run run-expb-reproducible-benchmarks.yml --ref perf/linux-block-priority-review \
  -f arch=amd64 -f expb_branch=perf/linux-priority-review \
  -f state_layout=flat -f payload_set=realblocks -f amount=1000 \
  -f docker_images=IMAGE -f rebuild_docker=false \
  -f expb_env='EXPB_NETHERMIND_PRIORITY_MODE=reth'
```

Run the `reth` command a second time, then the `observe` command a second time, to complete ABBA. Then repeat the same four dispatches with `-f arch=arm64`. The arm64 runner supports only the flat layout. For timing runs, prefer `payload_set=realblocks` with `amount=1000`; use `superblocks` with `amount=100` as a shorter smoke test. Choose a delay that fits the available run budget, and record the resolved inputs from every run. Use `nice` only when reproducing the historical `-5` experiment.

The timings from amd64 and arm64 are separate experiments; do not compare their absolute values. This prototype does not make a performance claim. It checks whether the intended per-thread state was applied and restored so that timing comparisons are interpretable.

### Fusaka ARM snapshot alignment

The current Fusaka payload corpus starts at block `25489990`, while the ARM runner's available snapshot is at `25490000`. For that corpus and snapshot pairing only, append the following to every arm so the ten pre-snapshot records are skipped and block `25490000` is the single unmeasured warmup; block `25490001` is then the first measured block:

```bash
  -f expb_env='EXPB_SKIP_OVERRIDE=10,EXPB_WARMUP_OVERRIDE=1,EXPB_NETHERMIND_PRIORITY_MODE=observe'
```

Use `EXPB_NETHERMIND_PRIORITY_MODE=reth` for the Reth arm and `observe` for its explicit control. Confirm the corpus start and snapshot head before reusing these values with another dataset. Older ARM payload datasets require their matching legacy snapshot at `/data/nethermind/nethermind-flat-snapshot`; do not point them at the Fusaka snapshot `/data/nethermind/nethermind-flat-25490000`.

## Verify the raw logs

Download or copy the `expb-run.log` artifact for each arm and run:

```bash
python scripts/priority-bench/verify_priority_log.py expb-run.log --mode observe
python scripts/priority-bench/verify_priority_log.py expb-run.log --mode reth
```

The verifier fails when there is no valid `EXPB_PRIORITY` record, any record reports `success=false`, the observe arm changes nice, the Reth arm does not report `nice_during=-20` or the fallback `min(nice_before, -6)`, or the policy/nice values are not restored to their original values. A fallback that preserves an already stronger baseline, such as `nice_before=-10`, is accepted but represents no additional raise; an already `-20` baseline is rejected as ineffective. A passing verifier is a precondition for interpreting timing output; it does not establish that one client is faster.
