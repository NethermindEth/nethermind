# EIP-8437 transport benchmarks

The harness runs separate Nethermind test processes over real RLPx/TCP or ethp2p/QUIC, with authenticated ETH66 transaction recovery. Linux network namespaces provide isolated, shaped links. Proof bytes, pool admission and verification are controlled fixtures; reported transaction goodput is **measured transport goodput under this workload, not native proving or block throughput**. Native measurements and their reproduction recipe are in `native-cost/`.

Requirements: Linux, .NET 10 SDK, Python 3, `iproute2`, `ethtool`, and `libmsquic`. Namespace creation needs root. Use a dedicated machine or bound the workload; the default matrix uses at most four node processes and 100 Mbit/s links. It creates no external listener outside its namespaces.

```sh
dotnet build src/Nethermind/Nethermind.Network.Test/Nethermind.Network.Test.csproj -c Release
export LEAN_NET_PREFIX=lean14
export LEAN_NET_SUBNET=10.78
python3 scripts/lean-net-bench/netbench.py run --netns \
  --test-dll "$PWD/src/Nethermind/artifacts/bin/Nethermind.Network.Test/release/Nethermind.Network.Test.dll" \
  --binding ethp2p --kind 1 --transactions-per-object 16 \
  --proof-bytes 293448 --objects 24 --burst 4 --interval-ms 1000 \
  --rtt-ms 50 --rate-mbit 100 --out /tmp/lean-runs --results /tmp/lean-results.jsonl
```

Run `matrix.py --plan` to inspect the matrix before executing it. It compares loads and aggregation counts, 64 KiB through 8 MiB proofs, slower/lossy links, a four-node relay chain, and kind-2 broadcast at one object per 12-second slot. It records every attempted run; incomplete delivery and drain time reduce goodput. Never reuse a capture directory. Use a fresh results file when changing source or binaries.

```sh
python3 scripts/lean-net-bench/matrix.py \
  --test-dll "$PWD/src/Nethermind/artifacts/bin/Nethermind.Network.Test/release/Nethermind.Network.Test.dll" \
  --out /tmp/lean-runs --results /tmp/lean-results.jsonl --repetitions 2
python3 scripts/lean-net-bench/report.py /tmp/lean-results.jsonl --out /tmp/lean-report
```

The report requires `matplotlib`. Open `index.html` locally, or view the standalone PNG/SVG files on mobile. Each capture contains node logs, timings, interface counters, source revision, binary hash and runtime details. The controller stops its processes and removes its namespaces when finished; `netbench.py teardown` removes only the configured prefix after an interrupted run. Existing devnets and sites are not used.

The receive budget is not a process-memory limit: native prover workspace and benchmark fixtures must be measured separately. The cold native folding timings include recursive aggregation work, while root verification and fixture generation are reported independently. Neither the small final proof size nor a successful transport run demonstrates that the provisional 4096-dependency block limit is safe.

## Matrix and interpretation

The planned matrix has 38 scenarios per repetition, executed serially. With two repetitions this is 76 attempted runs; connection setup, the all-node warmup barrier, idle calibration and draining add time beyond the roughly 17 minutes of scheduled release/warm-up windows. Use one immutable source archive and validated DLL for the entire matrix. Name each campaign independently and record its source revision, archive SHA256 and DLL SHA256. Earlier failed builds and smoke captures remain diagnostic evidence; keep them separate from the final comparison and never silently replace them. A source or binary change starts a new campaign. The report rejects inputs with different source, archive or DLL identities, including failed runs; render diagnostic campaigns separately. Captures without those identities remain diagnostic and cannot be mixed with identified runs.

| Group | Bindings | Workload | Link | Runs per repetition |
|---|---|---|---|---:|
| Aggregation/load | RLPx, QUIC | 1/16/64 distinct transaction envelopes per wrapper; 1/8/32/64 objects/s for six scheduled seconds | 100 Mbit/s per node, 50 ms RTT, no injected loss | 24 |
| Object size | RLPx, QUIC | 64 KiB, 1 MiB, 8 MiB proof payloads; six objects at one/s | 100 Mbit/s, 50 ms RTT | 6 |
| Link/topology | RLPx, QUIC | 24 objects, bursts of four each second | Pair at 10 Mbit/s/100 ms; pair at 100 Mbit/s/50 ms/1% loss; four-node chain at 100 Mbit/s/50 ms | 6 |
| Broadcast + retrieval fallback | QUIC | Kind-2 object, one per 12-second slot, three measured objects | 100 Mbit/s/50 ms, 0% and 1% injected loss | 2 |

Use the observed native SPHINCS root sizes for the three wrapper aggregation counts. These sizes calibrate synthetic transport payloads; the transport fixtures do not carry those native proofs or run the native verifier.

```sh
# Supply the validated assembly and exact frozen-source provenance.
: "${LEAN_BENCH_DLL:?Set the validated test assembly path}"
: "${LEAN_NET_SOURCE_REVISION:?Set the full source commit}"
: "${LEAN_NET_SOURCE_ARCHIVE_SHA256:?Set the source archive SHA256}"
export LEAN_NET_SOURCE_REVISION LEAN_NET_SOURCE_ARCHIVE_SHA256
export LEAN_NET_PREFIX=lnbench
export LEAN_NET_SUBNET=10.78
# Choose a fresh campaign name; never overwrite an earlier capture.
lean_capture=/tmp/lean-net-campaign-01
mkdir "$lean_capture"
printf '%s\n' '{"1":265232,"16":293448,"64":291464}' > "$lean_capture/proof-sizes.json"
python3 scripts/lean-net-bench/matrix.py --plan \
  --test-dll "$LEAN_BENCH_DLL" --out "$lean_capture/runs" \
  --results "$lean_capture/results.jsonl" --repetitions 2 \
  --proof-sizes "$lean_capture/proof-sizes.json"
# After smoke validation of this exact assembly, execute the identical command with --plan removed.
python3 scripts/lean-net-bench/report.py "$lean_capture/results.jsonl" \
  --native-dir scripts/lean-net-bench/results/2026-10-14/native \
  --out "$lean_capture/report"
```

Before the full matrix, both bindings must complete a maximum 8 MiB proof transfer with no receive-budget starvation, and a 16-transaction wrapper must recover its envelopes over authenticated ETH66. Check source/archive/DLL hashes, all expected receiver deliveries, peer counts and decoder-budget diagnostics. A smoke success permits measurement; it does not establish a memory upper bound. Preserve failed attempts, node logs, counter snapshots and the exact arguments alongside successes. Stop and investigate repeated resource or protocol errors before continuing high-load cases. After interruption, clean up only the task's namespace prefix and node processes; no devnet or website is needed.

Transaction goodput means completed, validated wrapper envelopes per receiver, multiplied by transaction count. Completion is signalled before controlled pool admission; it is not a count of successful native verifications or block inclusions. Each transaction/dependency is distinct within the workload. Useful Mbit/s counts canonical object bodies, including proof bytes; it excludes separately recovered ETH envelopes. Interface `wire_bytes` includes namespace traffic between the recorded counter reads, including ETH recovery, protocol overhead and retransmissions; it must not be relabelled as proof payload bytes. Its baseline finishes before common-start publication, so the window includes post-warmup idle settling as well as measurement and drain. Counter-read timestamps are retained; aborted counter windows are ineligible for wire comparisons.

Every node signals only after validating/recovering all warmup objects, including broadcast warmup and each node in a relay chain. The controller waits within a bounded timeout before publishing a common measured start at least four seconds in the future, leaving two seconds for idle calibration. Broadcast starts align to the next shared 12-second profile slot after that minimum, with a 100 ms boundary guard, so warmup and the first measured object cannot reuse the same duty. A zero-warmup node signals immediately. Measured origin/fetch tasks also await their own start snapshot; no measured origin starts before that snapshot. Warmup failure keeps the failed attempt, completed-node timestamps and logs. `warmup_barrier` records the controller observation and common start; node files retain their completion and actual snapshot timestamps. Broadcast manifests use the shared profile clock at actual release, so moving the measured start does not change the profile genesis.

The scheduled offered rate is the configured release schedule. The controller also records actual released objects and their first/last times. Actual origin rate divides that count by the first-to-last release span plus one configured interval, so two bursts at zero and one second cover two release intervals. Schedule-lateness p50/p95/max measures actual origin time minus its scheduled release. These are workload observations, not native capacity guarantees. Latency starts at actual origin execution; each object contributes the latency of its slowest receiver after every receiver completes. This is not a per-transaction percentile or a link RTT. It ends at validated receiver completion, so it includes origin admission/recovery/transfer work after that point but excludes scheduler lateness before the origin starts. Goodput divides by the maximum of the scheduled window, controller-observed stop, and the last recorded node activity. Controller stop follows end-counter reads, so the denominator includes bounded observation overhead and queue drain. Partial delivery reduces goodput; latency percentiles include only objects completed at every receiver. A high-loss run can therefore have plausible latency among its survivors while remaining incomplete. Error rows and missing curve points must be read with the failure table, not interpreted as successful capacity.

Broadcast-enabled measurements exercise actual coding, QUIC delivery and a test producer's secp256k1 signature recovery alongside on-demand retrieval fallback. Completion can come from either path; these measurements do not isolate pure broadcast capacity. Both lifetime and start-to-stop accepted-shard counters are retained; the measured delta excludes warmup and confirms coded-shard usage during measurement, without establishing which path completed an object first. The test profile accepts every reconstructed consensus context; these results do not validate a live CL authorization/anchor integration. The pair/chain layouts are bounded transport experiments, not a public adversarial network.

Recommendations should follow measured completion, bytes, latency, CPU, memory and loss together: compare the two bindings at the same workload and link; identify the offered-load range that still delivers all objects; show large-object and relay penalties separately; and retain resource failures. Native cold aggregation/verification graphs belong in their own panel with the M2 Max/one-worker scope and the trivial generic-program caveat. The data cannot establish that the provisional dependency weight of 16, the draft 4096-dependency capacity, or a one-second aggregation cadence is universally safe. No Lean fork or prover optimization is part of this experiment. Parameter recommendations remain conditional until the matrix is complete: distinguish a tested configuration from a demonstrated threshold, and avoid claiming a binding is faster when completion or actual origination differs. The separate 200 ms RTT comparison uses the same assembly, 16 transactions/object, 293448-byte proofs, 8/32 objects/s and two repetitions per binding. Keep its `rtt200-*` labels outside `load-*`; it measures the receive-credit tradeoff and does not by itself prove that a larger window is safe.

Reporting-only checks run without services or native proofs:

```sh
python3 scripts/lean-net-bench/test_metrics.py
python3 -O scripts/lean-net-bench/test_metrics.py
```

CPU counters are raw process deltas between each node's actual `snapshot_before_ns` and `snapshot_after_ns`; `cpu_ms_total` remains authoritative for those recorded windows. The idle-adjusted value is only an estimate. `idle_snapshot_ns` and warmup-completed counts at idle/start expose late sampling and unfinished warmup; the fixed settling delay alone proves neither; historical diagnostic captures made before the warmup barrier can contain warmup traffic in the measured window. CPU comparisons are eligible only when every node has all warmup objects complete at idle/start, both snapshots begin within 10 ms of their scheduled times, the idle interval lasts at least 1.9 s, and no measured origin/completion precedes the start snapshot. Otherwise the adjusted estimate is null and CPU comparisons must be omitted. Raw counters, timing metadata and reasons remain available; these checks do not establish host isolation or a process-memory bound.

### Report views and comparison scope

`report.py` produces mobile PNGs and SVGs with a scope note inside each figure, plus an HTML failure and resource table. The `matched-rtt` panel includes only 50/200 ms pairs with identical remaining workload and link arguments; unmatched attempts remain in the all-runs table. The link panel compares bindings within each configuration. It cannot isolate relay overhead without a matching healthy-pair workload, and none of these views measures control-message or head-of-line latency. The broadcast panel is explicitly broadcast plus retrieval fallback: measured accepted shards exclude warmup but do not identify the winning completion path.

The overload table keeps measured fetch starts, reassemblies and expiry events per attempt. Its minimum-never-fetched value is only a lower bound; retries prevent exact unique-object attribution from those counters. Figure warnings retain recorded expiries alongside missing counts.

The resource panel shows 16-transaction load cases. Idle-adjusted CPU requires complete runs and explicitly eligible snapshots; the denominator is scheduled objects and CPU is summed across nodes. RSS is the maximum node process lifetime peak, including setup. The wire ratio is summed all-node interface transmit bytes divided by canonical body bytes times validated receiver deliveries. ETH recovery, control, retransmissions and idle settling remain in its numerator; it is not pure framing overhead. Complete eligible counter windows are required. Missing values stay blank, while the HTML table retains raw CPU/TX and failures.

Figures use medians and observed min–max ranges; these ranges are not confidence intervals. The native capture README's existing timing table reports means, separately labelled. Native plots include the host/worker count and trivial-STARK caveat; they are not transport measurements or slot-capacity guarantees. `report-source.json` records the rendering script SHA256 separately from the immutable measured-source identities in `results.json`. A later reporting-only revision does not change any captured measurement source. Keep measured observations separate from any proposed specification thresholds.

To combine the final matrix and its matched long-RTT supplement, pass both files to the same report invocation; their measured source/archive/DLL identities must agree:

```sh
python3 scripts/lean-net-bench/report.py "$lean_capture/results.jsonl" \
  "$lean_capture/long-rtt-results.jsonl" \
  --native-dir scripts/lean-net-bench/results/2026-10-14/native \
  --out "$lean_capture/report"
```

The rendering regression in `test_metrics.py` runs when `matplotlib` is installed; it covers complete, partial and error rows without services or native proving.
