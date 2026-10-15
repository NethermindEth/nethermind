# Short-burst diagnostics

[PNG](quic-delivery.png) · [SVG](quic-delivery.svg) · [events](timelines.csv) · [provenance](timelines.json)

These are separate, single-sample V8 and V9 diagnostics, not the final campaign. Each scheduled six 32-wrapper bursts, one second apart, with 16 transactions per wrapper over a 50 ms / 100 Mbit/s pair. The verifier and pool admission are controlled; transaction recovery uses real ETH. Warmup is excluded.

V8 delivered 154/192 wrappers by 14.10 s; V9 delivered 142/192 by 11.99 s. The remaining 38/50 wrappers were never fetched. The graph retains the full 79.88/77.47 s observation windows, including the wait for missing objects. Neither the reported whole-window goodput nor the early ramp establishes steady-state capacity. No throughput curve is fitted.

From the repository root, using Python with matplotlib 3.9.4:

```sh
python3 scripts/lean-net-bench/results/2026-10-15/network/diagnostics/extract_timelines.py
python3 scripts/lean-net-bench/results/2026-10-15/network/diagnostics/render_timelines.py
```

The extractor reads exact members directly from the adjacent [V8](../archives/baseline-v8.tar.gz) and [V9](../archives/pipeline-diagnostic-v9-tx16.tar.gz) archives, checking their member hashes against the external manifests. It checks measured object IDs and completion/fetch counts against the summaries. `timelines.json` records archive/member hashes and each measured source, source archive and DLL identity separately from these reporting scripts. No archive extraction or runtime execution is needed.

Events use each node's recorded measured start. Receiver and origin starts differ by 31.420 μs for V8 and 21.811 μs for V9; these offsets are retained in metadata. The inset enlarges the same events. See the [harness methodology](../../../../README.md) for measurement conventions.
