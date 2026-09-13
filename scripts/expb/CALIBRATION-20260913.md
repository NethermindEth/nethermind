# EXPB calibration — 2026-09-13

The two runners are separate experiments. All runs below use the flat Fusaka
snapshot, 1,000 measured payloads and client image
`nethermindeth/nethermind:master-d7bd8d0`. Its multi-platform digest is
`sha256:7f6d0a147e445c14103d4afb9b3567e43f292e33f90128e7dd44d7c294869a78`.

## Cleanup fix

EXPB kept the payload server's SSE connection open while stopping Nethermind.
Closing RPC consumers before the client removes the approximately 60-second RPC
shutdown wait. The fix is in
[EXPB PR #32](https://github.com/NethermindEth/execution-payloads-benchmarks/pull/32),
revision `4a7ef676493fedfc4973c2d3420442d71dedd255`.

| Runner | Previous cleanup, mean of 3 | Patched cleanup, 1 smoke run |
| --- | ---: | ---: |
| amd64 | 67.97 s | 8.04 s |
| arm64 | 65.58 s | 5.71 s |

Cleanup is elapsed time from workload completion to EXPB's cleanup-completed
event. This establishes a teardown improvement, not a block-processing improvement.
Both smoke runs contain the normal Nethermind shutdown marker. The ARM smoke
used a one-block-shifted payload window and is used only for cleanup evidence.

Sources: old [amd64](https://github.com/NethermindEth/nethermind/actions/runs/34750625006)
and [arm64](https://github.com/NethermindEth/nethermind/actions/runs/34750632267)
runs; patched [amd64](https://github.com/NethermindEth/nethermind/actions/runs/34751189559)
and [arm64](https://github.com/NethermindEth/nethermind/actions/runs/34751014612)
smokes.

## Baseline health and alignment

The original amd64 harness produced SSE run means of 25.5943, 25.3414 and
25.2178 ms: sample CV **0.756%**, n=3, with 999 SSE observations and 1,000 K6
table rows per run. The usual SSE stream omits the last block because its timing
is queried when the next payload is requested.

All three original ARM repetitions logged exceptions while replaying older
warmup blocks, although the old workflow reported success. Those timings are
excluded. The snapshot has no state for those older blocks.

The aligned calibration uses
`EXPB_SKIP_OVERRIDE=11,EXPB_WARMUP_OVERRIDE=0`, preserving measured blocks
25,490,001–25,491,000 while avoiding replay before the snapshot. This changes
startup history; compare modes only within this explicitly recorded setup.

## Sequential standard campaigns

Both campaigns passed all three repetitions with the full image digest. Every
sample delivered 1,000 K6 payload rows and 999 SSE timings, with the identical
SSE block sequence 25,490,001–25,490,999. All samples contained normal shutdown,
had no exceptions or invalid blocks, and verified absent containers, mounts and
writable snapshot contents after cleanup.

| Runner | SSE run means (ms) | Across-run CV, n=3 | Sum of sample elapsed time | Mean cleanup |
| --- | --- | ---: | ---: | ---: |
| amd64 | 25.2095, 25.3292, 25.4269 | 0.430% | 207.83 s | 8.24 s |
| arm64 | 24.5467, 25.0034, 24.5929 | 1.017% | 143.64 s | 5.66 s |

Sample elapsed time includes rendering, execution, teardown and verification;
it excludes the job's one-time checkout/install and artifact upload. These are
separate machine measurements. Three observations provide only a preliminary
noise estimate. The changed startup alignment also prevents attributing a CV
change versus the old harness to batching alone.

Sources and complete campaign artifacts:
[amd64](https://github.com/NethermindEth/nethermind/actions/runs/34752398003),
[arm64](https://github.com/NethermindEth/nethermind/actions/runs/34752400728).

## Warmed calibration failure

The ARM compute-warm experiment repeatedly logged
`System.InvalidOperationException: Cannot move unknown block ... to main`
from `BlockTreeOverlay.ResetMainChain` through the pooled simulation
environment. Raising the gas cap does not resolve this client error. The runner
then failed with `No space left on device` while writing its Actions diagnostic
log and went offline before uploading the campaign artifact. This is not an
accepted warmed performance sample.

The matching amd64 warmed experiment was cancelled rather than used for a
comparison. No warmed CV is claimed for this image. Complete warmup and client
health checks must pass before this mode can be used for regression screening.

Sources: [ARM failed run](https://github.com/NethermindEth/nethermind/actions/runs/34752625718),
[amd64 cancelled run](https://github.com/NethermindEth/nethermind/actions/runs/34752624438).

## GC diagnostic, ARM only

Two separate timeline captures used the aligned payload window. These are
diagnostic captures, not performance samples: neither recorded the normal
Nethermind shutdown marker under the profiling wrapper.

| Setting | Capture window | Collections | Total GC pause | Large induced Gen1 pauses |
| --- | ---: | ---: | ---: | --- |
| Default sweep | 46.4 s | 16 | 780.5 ms | 149.1, 268.1, 288.6 ms |
| `--Merge.SweepMemory=NoGC` | 47.0 s | 14 | 170.9 ms | None |

The second capture still contains Gen2 collections. `NoGC` disables the
post-block sweep strategy's collections, not all runtime GC. These single
captures justify an unprofiled experiment; they do not establish a CV reduction.
The trace window includes startup and shutdown work, so its total pause time
must not be subtracted from measured block timings.

Sources: [default sweep](https://github.com/NethermindEth/nethermind/actions/runs/34751229048),
[NoGC sweep](https://github.com/NethermindEth/nethermind/actions/runs/34751583002).
Reports were generated from the EventPipe sidecars with
`dotnet run --file scripts/nettrace-report.cs -- <capture.nettrace>`.
