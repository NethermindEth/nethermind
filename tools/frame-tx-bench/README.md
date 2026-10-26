# frame-tx-bench

A CLI command center for the EIP-8141 `MAX_VERIFY_GAS` measurement campaign, built from
[the benchmarking plan](https://hackmd.io/@deanstef/BJu3k-Mvfl) ("Benchmarking VERIFY frame gas
ceiling"). It catalogs the `[Explicit]` NUnit fixtures already in the tree, drives `dotnet test`
the same way `.github/workflows/run-frame-tx-measurements.yml` does, and turns the `RESULT
key=value` rows those fixtures write into the tables, a plan-shaped summary, and charts.

No third-party dependency is required. If `matplotlib` is installed, `report` writes PNG charts;
otherwise it prints an ASCII fallback.

## Usage

```bash
cd tools/frame-tx-bench

# See the whole campaign: 5 fixtures, 126 input combinations across shape x ceiling x K_retry x rate.
./frame_tx_bench.py list

# See one fixture's exact sweep points.
./frame_tx_bench.py list --fixture FrameTxFloodMeasurement

# Run a fixture (builds once, sets FRAME_*_OUT, pins FrameTxFloodMeasurement to taskset -c 0).
./frame_tx_bench.py run --fixture FrameTxMempoolDosMeasurement --groth16-artifacts /path/to/artifacts

# Run everything a given ceiling needs, including a MaxVerifyGas const patch for >300k points
# (reverted automatically when the run finishes or fails).
./frame_tx_bench.py run --harness flood --ceiling 500000 --raise-verify-gas 510000

# Preview commands without running them.
./frame_tx_bench.py run --harness flood --dry-run

# Turn a run's RESULT rows into tables + the plan's metric summary + charts.
./frame_tx_bench.py report --in out/2026-09-02T160806Z/*-results.txt --summary \
    --budget-us 2000 --attack-rate 100 --csv --out-dir report/
```

`report --summary` prints the plan's own metric tables directly from `RESULT` rows:

- `t_reject(C)` — admission reject latency (p50/p99), from `case=frame_reject`/`signature_reject`.
- `W_0` / `W(C,r,K_retry)` / `Δ` — block-processing delay under flood, from `case=flood_delay` and
  `case=production_under_flood`.
- `R_max(C, K_retry)` — sustainable reject rate, from `case=r_max`/`r_max_with_retries`.
- `C*` — the plan's decision rule (`Δ(C, r_attack, K_retry) ≤ B`), computed on request via
  `--budget-us B --attack-rate r_attack`; reports the largest ceiling ≥ 236k that satisfies it per
  shape and K_retry.

`report` without `--case`/`--summary` prints every `case=` group found as a generic table (all
columns the rows carry), which covers any RESULT row shape without needing this script to hardcode
every field name the harnesses emit.

## Design notes

- **Fixture-level `dotnet test` runs, not per-`[TestCase]`.** NUnit/VSTest's `--filter`
  mini-language treats bare `(`/`)` as grouping operators, so filtering to one exact `[TestCase]`
  is not reliably scriptable. `run` always runs a whole fixture (matching the CI workflow itself);
  `--ceiling`/`--harness`/`--fixture` narrow *which fixtures* run, and `report --case` slices the
  resulting rows afterward.
- **`--raise-verify-gas`** mirrors the workflow's own safety checks: refuses anything below 500000
  (nothing below that unlocks a sweep point), requires exactly one match of the stock literal
  before patching, and always reverts the file in a `finally` block — including on `--dry-run`,
  which never touches the source file at all.
- The fixture catalog (`FIXTURES` in `frame_tx_bench.py`) mirrors the current `[TestCase]`
  attributes on `FrameTxVerifyDosMeasurement`, `FrameTxMempoolDosMeasurement`,
  `FrameTxFloodMeasurement`, `FrameTxProducerRetryMeasurement`, and `FrameTxPrefixRetryMeasurement`.
  If a harness's sweep changes, update the matching `combos` list here too.
