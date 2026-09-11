# Linux priority experiment

This is a small, opt-in experiment for checking whether Nethermind's block-processing priority handling changes on Linux. The EXPB branch passes one of two modes into the Nethermind container:

- `observe`: request the existing managed `Highest` priority and record the effective Linux policy and nice value without changing the nice value.
- `nice`: request the same managed priority and temporarily set the processing thread's nice value to `-5`, restoring it when the scope ends.

The two modes receive the same Docker `SYS_NICE` capability. The normal path is unchanged when `EXPB_NETHERMIND_PRIORITY_MODE` is absent or `off`.

## A/B dispatch

Before starting, publish the Nethermind `perf/linux-block-priority-prototype` branch (which contains the workflow and verifier), the EXPB `perf/linux-priority-prototype` branch, and a Nethermind prototype image containing the runtime change with both amd64 and arm64 manifests. Use a unique commit-based image tag for every arm and check that its registry digest is unchanged before and after the runs; the current workflow renderer requires tag references in `docker_images` and reconstructs the image from the tag. The workflow's `docker_images` input selects explicit images and `rebuild_docker=false` prevents a build. Keep `state_layout`, `payload_set`, `amount`, `delay_seconds`, `additional_extra_flags`, `flat_write_buffer_floor`, `run_count`, and profiling inputs identical.

Run an ABBA sequence independently on each architecture: observe (A), nice (B), nice (B), observe (A). Wait for each dispatch to finish before starting the next one because the benchmark runner is shared. Use fresh workflow dispatches, keeping the image and all other inputs unchanged. For example, replace `IMAGE` with the same prebuilt image reference in all four commands:

```bash
gh workflow run run-expb-reproducible-benchmarks.yml --ref perf/linux-block-priority-prototype \
  -f arch=amd64 -f expb_branch=perf/linux-priority-prototype \
  -f state_layout=flat -f payload_set=realblocks -f amount=1000 \
  -f docker_images=IMAGE -f rebuild_docker=false \
  -f expb_env='EXPB_NETHERMIND_PRIORITY_MODE=observe'

gh workflow run run-expb-reproducible-benchmarks.yml --ref perf/linux-block-priority-prototype \
  -f arch=amd64 -f expb_branch=perf/linux-priority-prototype \
  -f state_layout=flat -f payload_set=realblocks -f amount=1000 \
  -f docker_images=IMAGE -f rebuild_docker=false \
  -f expb_env='EXPB_NETHERMIND_PRIORITY_MODE=nice'
```

Run the `nice` command a second time, then the `observe` command a second time, to complete ABBA. Then repeat the same four dispatches with `-f arch=arm64`. The arm64 runner supports only the flat layout. For timing runs, prefer `payload_set=realblocks` with `amount=1000`; use `superblocks` with `amount=100` as a shorter smoke test. Choose a delay that fits the available run budget, and record the resolved inputs from every run.

The timings from amd64 and arm64 are separate experiments; do not compare their absolute values. This prototype does not make a performance claim. It checks whether the intended per-thread state was applied and restored so that timing comparisons are interpretable.

### Fusaka ARM snapshot alignment

The current Fusaka payload corpus starts at block `25489990`, while the ARM runner's available snapshot is at `25490000`. For that corpus and snapshot pairing only, append the following to every arm so the ten pre-snapshot records are skipped and block `25490000` is the single unmeasured warmup; block `25490001` is then the first measured block:

```bash
-f expb_env='EXPB_SKIP_OVERRIDE=10,EXPB_WARMUP_OVERRIDE=1,EXPB_NETHERMIND_PRIORITY_MODE=observe'
```

Use `EXPB_NETHERMIND_PRIORITY_MODE=nice` for the nice arm. Confirm the corpus start and snapshot head before reusing these values with another dataset; old ARM payload datasets require the snapshot corresponding to their own starting range.

## Verify the raw logs

Download or copy the `expb-run.log` artifact for each arm and run:

```bash
python scripts/priority-bench/verify_priority_log.py expb-run.log --mode observe
python scripts/priority-bench/verify_priority_log.py expb-run.log --mode nice
```

The verifier fails when there is no valid `EXPB_PRIORITY` record, any record reports `success=false`, the observe arm changes nice, the nice arm does not report `nice_during=-5`, or the policy/nice values are not restored to their original values. A passing verifier is a precondition for interpreting timing output; it does not establish that one client is faster.
