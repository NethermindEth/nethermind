# Aggregation scheduling decisions

These are controlled decision checks, using synthetic statement-checked
dependency proofs and a manually advanced one-second timer. One scenario admits
a real secp256k1 signed FrameTx to the production pool and builds its actual block
body. No Lean performance, throughput or native one-second completion claim is
made. No services were started for these checks.

The immutable [baseline capture](before.json) records production source
`c574cbb16a576c5121c1b0f9017920b776ab33a8`, with the uncommitted scheduling harness
identified by actual assembly SHA256 hashes inside the capture. Its seven rows
passed the baseline assertions. It was rebuilt in a temporary detached checkout
using the same revised harness as [the initial after capture](after-initial.json).
The temporary checkout was removed after capturing the results.
The final [after capture](after.json) uses production main revision
`4a17b571f453d112bae5175a2e4966900201deae`, merged into benchmark child revision
`0d33ebadf13143f4af767ff558fa625c3fc622cf`. Both baseline and final after passed
seven rows; after additionally enabled `--require-fresh=true`.

![Recorded scheduling decision counts](decisions.svg)

| Decision | Before | After |
| --- | ---: | ---: |
| Extra builder prove after background proof | 1 | 0 |
| Eligible completed wrapper deliveries while new proving is blocked | 0 | 1 |
| Removed selection delivered after proving completes (0=no, 1=yes) | 1 | 0 |

Both runs retain one active proving call, skip proving across 120 unchanged
wrapper selections, reuse a proof after a transaction-body change, verify all
120 exact-parent builder reuse attempts, reject a damaged parent, and refresh a
completed wrapper without proving again. The 1/3/10-second values are virtual
blocked-clock scenarios, not measured native latency or throughput.

[before-original.json](before-original.json) preserves the earlier baseline
before the peer warmup accounting refinement. [after-initial.json](after-initial.json)
is an intermediate successful seven-row run at
`2d64c3b44b0e145a04a36d3208fecb2582eaf72c`, before subsequent client review fixes.
The final comparison uses `before.json` and `after.json`.
Neither historical file is relabeled as the final after result.

The identical baseline, initial-after and final-after harness sources had these SHA256 hashes:

| File | SHA256 |
| --- | --- |
| `SchedulingChecks.cs` | `58bece585e17db75d54300a399f35fc5de179b749a63e933ddea22dda7a0a017` |
| `Program.cs` | `211e47f4773444789bfcf4d5c4fcc169ed8655af21e9d001de03d92e40459813` |

Run the checks using [scheduling.md](../../../scheduling.md). Reproduce the mobile
SVG from the immutable captures without additional dependencies:

```sh
python3 tools/LeanBench/scheduling_plot.py \
  tools/LeanBench/results/2026-10-04/scheduling/before.json \
  tools/LeanBench/results/2026-10-04/scheduling/after.json \
  --out=tools/LeanBench/results/2026-10-04/scheduling/decisions.svg
```

Proof-call counters cover the controlled backend. The real native integration
tests supply cryptographic validation separately. Virtual timer notifications
can coalesce while work is active; a tick is not a proof completion or a
throughput sample.
