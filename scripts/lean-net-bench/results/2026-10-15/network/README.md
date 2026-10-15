# EIP-8437 transport measurements

[HTML report and tables](report/index.html) · [Recommendations](recommendations.md) · [Checksums](SHA256SUMS)

Both bindings delivered every 8 MiB proof-object cohort. High-load results varied with transaction count, RTT and repetition; these short-burst observations do not establish steady-state capacity.

## Scope and provenance

There are 76 main attempts plus eight matched 200 ms RTT attempts: two repetitions per configuration. All 84 are retained, including 23 partial high-load cohorts and 13 measured assembly-expiry events. No unexpected measured protocol/local-failure events, peer loss, redials or nonzero child exits were recorded. Ten CPU snapshots are ineligible; 55 runs are both complete and CPU-eligible.

| Measured identity | Value |
| --- | --- |
| Source | `0178a8284058611a1a303d7608bd7f8600ecf1db` |
| Source archive SHA256 | `c5604b1b4acf9e239d40405071941f7258f44f75f37b35c964d740c715d8f910` |
| Test DLL SHA256 | `c98cbcb695201ebe74209bc6e3f5fda38211b86b3e9169720f300b10738b906a` |
| Packaged raw evidence SHA256 | `ff777ec40d057f04cb7f0588b78c9da2349b3cc69cee4e447e1d5984737d72fb` |

The Linux VM exposes six vCPUs (AMD EPYC 7642), about 15.6 GiB RAM, .NET SDK 10.0.401/runtime 10.0.12; benchmark children use `DOTNET_PROCESSOR_COUNT=2`. [Host provenance](host-provenance-v12.json) is explicitly a packaging-time snapshot; per-run counters remain authoritative. The [reporting-script hash](report/report-source.json) is separate from the captured source. Subsequent code changes do not relabel these binaries or erase baseline events.

Production RLPx/TCP or ethp2p/QUIC and authenticated same-peer ETH recovery carry distinct frame envelopes and declared dependencies. Proof verification and pool admission are controlled. Delivery is not fresh native proving, real mempool policy or block execution throughput. The [harness README](../../../README.md) explains the full methodology.

## Observed operating points

For the 50 ms RTT, 100 Mbit/s, zero-injected-loss pair, these are the highest **tested** scheduled rates with both repetitions complete. Goodput includes the six-second release schedule and full drain/observation window. These are not maximum sustained rates.

| Binding | Transactions/object | Scheduled objects/s | Complete cohorts | Median delivered objects/s |
| --- | ---: | ---: | ---: | ---: |
| RLPx + ETH | 1 | 32 | 2/2 | 31.65 |
| RLPx + ETH | 16 | 32 | 2/2 | 32.00 |
| RLPx + ETH | 64 | 8 | 2/2 | 8.00 |
| QUIC + ETH | 1 | 8 | 2/2 | 6.53 |
| QUIC + ETH | 16 | 8 | 2/2 | 7.91 |
| QUIC + ETH | 64 | 8 | 2/2 | 7.39 |

QUIC one-transaction objects at 32/s delivered 110/192 and 192/192; retain this range rather than infer a stable ceiling from either run. Larger transaction counts also increase ETH recovery work: 64 envelopes require four sequential batches of 16, alongside per-transaction handling. Similar proof sizes do not isolate cryptographic cost. The 64/s cases release 384 objects, exceeding the local validated-object store's 256-object ceiling (128 MiB); availability, deferred queues and expiry can all affect completion. The counters do not identify a single cause.

[Load PNGs](report/goodput-tx1.png): [16 transactions](report/goodput-tx16.png) · [64 transactions](report/goodput-tx64.png) · [Resources/wire](report/resource-wire.png)

## RTT, large objects and fallback

The matched RTT cases use 16 transactions/object and identical remaining arguments. All 8/s cohorts completed. At 32/s, delivered counts were:

| RTT | RLPx + ETH, out of 192 | QUIC + ETH, out of 192 |
| --- | --- | --- |
| 50 ms | 192, 192 | 179, 165 |
| 200 ms | 137, 133 | 90, 112 |

At 200 ms / 8/s, median whole-window goodput was 8.00 objects/s RLPx and 5.89 QUIC; median complete-run p95 was 1.732 s and 3.174 s. This tests the configured window and recovery pipeline, not an alternative receive-window sweep. [RTT PNG](report/matched-rtt.png).

Every size, shaped-link, chain and broadcast attempt completed. At 1% injected loss and 4 objects/s offered, RLPx p95 was 17.9/18.3 s with 1.04/1.03 objects/s goodput; QUIC p95 was 4.3/10.2 s with 2.56/1.78 objects/s. These two attempts per binding show a loss-sensitive case, not a universal ranking. At 8 MiB proof size, median goodput was 0.985 objects/s RLPx and 0.714 QUIC; median p95 was 1.229 s and 6.364 s. Each broadcast attempt delivered 3/3 objects with 48 measured / 64 lifetime accepted shards. This demonstrates coded participation with retrieval fallback, not the winning path or real CL authorization. [Large-object PNG](report/large-objects.png) · [Link/topology PNG](report/link-topology.png) · [Broadcast PNG](report/broadcast-fallback.png).

## Failures, metrics and reproduction

The [classification](classification-v12.json) preserves 15 startup log messages before common measurement start: 12 peer-abort reads and three control-FIN violations. They are distinct from reset measured counters, which retain 13 expiry events in the main campaign and none in the RTT supplement. Whole-run logs are not error-free. The overload table shows raw fetch/reassembly/expiry counters; minimum never fetched is only a lower bound, not a unique-object history.

Separate [teardown validation](archives/teardown-validation-summary.json) reproduced a false control-FIN violation during normal `NoError` close using the V12 production DLL. An isolated atomic close-order patch then passed 60/60 repeated cases, 18/18 QUIC cases and 252/252 stream/transport cases, with zero skips. The [archive](archives/teardown-validation.tar.gz) retains exact patch/DLL hashes, the corrected baseline failures, the initial framing-fixture mistake and a test-count policy error. These later regressions do not rerun or relabel V12; finite repetitions do not cover every scheduling interleaving.

Medians/min–max ranges describe two attempts, not confidence intervals. Timeout-heavy denominators can span roughly 80 seconds after six scheduled release seconds; their low goodput is not an active wire service rate. CPU comparisons require complete, eligible snapshots. RSS is a lifetime process peak, including fixtures/setup; TX/useful includes ETH envelopes, control, retransmissions and idle settling. UDP receive-buffer errors were zero; tc queue-drop counters were not captured. There is no isolated relay-overhead or control/HOL latency measurement.

[Main rows](main.jsonl) · [RTT rows](rtt200.jsonl) · [Raw archive](archives/final-v12.tar.gz) · [Per-file manifest](archives/final-v12.manifest.json)

```sh
python3 scripts/lean-net-bench/report.py \
  scripts/lean-net-bench/results/2026-10-15/network/main.jsonl \
  scripts/lean-net-bench/results/2026-10-15/network/rtt200.jsonl \
  --native-dir scripts/lean-net-bench/results/2026-10-14/native \
  --out /tmp/eip8437-report
```

Use matplotlib 3.9.4. The archive includes original run commands, configurations, logs and guard scripts, without source/binary archives or proof blobs. The authoritative campaign recipe is the archived `eip8437-v12-guarded-campaign.py` plus `matrix-v12/commands.jsonl` and `long-rtt-v12/commands.jsonl`, with their recorded argv/environment and host prerequisites. The generic `matrix.py` plan does not schedule the separate RTT supplement. [V8/V9 diagnostics](diagnostics/README.md) remain separate single-sample sources. [Native costs](../../2026-10-14/native/README.md) reuse the existing real-proof capture on a different host/profile; they are not a combined transport/proving throughput measurement.
