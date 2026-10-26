# frame-tx-kurtosis-devnet

Multi-client Kurtosis devnet for the EIP-8141 `MAX_VERIFY_GAS` campaign. Reproduces the
attack shapes from `tools/frame-tx-bench` under real network conditions: two execution
clients, a consensus layer, gossip, mempool propagation, block production, cross-client
processing.

Complements the in-process harnesses. Does not replace them. Collects raw measurements only:
analysis, aggregation and statistics are a follow-up task.

## Architecture

```
scenarios/base.yaml  ──►  runner/run_scenario.py  ──►  kurtosis run .
   topology                renders frame_tx block          │
                                                           ▼
                                              main.star (this package)
                                                           │
                        ┌──────────────────────────────────┼─────────────────────┐
                        ▼                                  ▼                     ▼
          ethereum-package run()              src/scenario.star        src/traffic.star
          nethermind + ethrex + CL            pins per-ceiling image   adds the generator
          prometheus + grafana + dora         and the ceiling flag     to the enclave
```

The package is a thin wrapper. It imports upstream `ethereum-package` and calls its `run()`,
then adds one service of its own. No fork, no vendored copy.

| Path | What it is |
|---|---|
| `main.star` | Entry point. Splits args, runs upstream, launches traffic. |
| `src/scenario.star` | Validates the `frame_tx` block, pins client images and the ceiling flag. |
| `src/traffic.star` | Adds the traffic generator to the enclave with the node endpoints. |
| `scenarios/base.yaml` | Shared topology. The runner appends the per-scenario block. |
| `images/` | Patched client images and the generator image. |
| `traffic/` | The generator (Python). |
| `runner/` | One-scenario and matrix drivers. |
| `results/` | Raw output, one directory per scenario. Not in git. |

## Why ethereum-package

It already supports both clients under test as first-class `el_type` values, and
`lambdaclass/ethrex` ships `fixtures/networks/eip8141-devnet.yaml`, the only proven
EIP-8141 configuration for it. That fixture is where this package's fork schedule comes from.

Searched and not found: any EIP-8141-specific Kurtosis package, and any upstream frames-devnet
config repository. `ethpandaops/frames-devnets` does not exist. The real upstream label is
`frames-devnet-0`, used by `ethpandaops/eth-client-docker-image-builder` and
`ethpandaops/hive-tests`. The branch name `eip8141-frame-txs-devnet7` refers to the
glamsterdam-devnet-7 base stack, not a frames devnet series. There was no genesis or network
config to inherit.

## Upstream dependencies

| Dependency | Pin | Why |
|---|---|---|
| `github.com/ethpandaops/ethereum-package` | `main`, imported at run time | Network, genesis, CL, Prometheus, Grafana. |
| `lambdaclass/ethrex` | `ETHREX_REF`, default `main` | EIP-8141 is on `main`. Built from source because the ceiling is a compile-time constant. |
| `soispoke/minimal-shielded-pool` | `devnet/frametx.py`, pinned by sha256 | The frame-transaction encoder, and the only real shielded-pool material. |
| `NethermindEth/frame-verify-gas` | release tag, fetched separately | Groth16 `verifier.hex` and `calldata-invalid.hex` sweeps. |
| Nethermind | this checkout, `HEAD` | EIP-8141 is not on `master`. It lives on `eip8141-frame-txs-devnet7` and descendants. |

The generator fetches `frametx.py` at image build time, verifies its sha256 against a pin and
fails the build on drift. Nothing third-party is committed to this repository.

## Gas ceiling: how it is applied, per client

This is the part that does not work the way it looks like it should.

**Nethermind** has `ITxPoolConfig.FrameTxMaxVerifyGas`, and it is not sufficient on its own.
Its own documentation: it bounds the declared-gas check only. A validation prefix that has to
be simulated is capped frame by frame at the compile-time `Eip8141Constants.MaxVerifyGas`,
and signature verification is capped there too. Raising the flag past that constant does not
move either. So the image carries the constant and the flag carries the same value.

**ethrex** has `pub const FRAME_TX_MAX_VERIFY_GAS: u64 = 100_000` and no flag, no config, no
environment variable. Every ceiling in this campaign is above it, so every scenario needs a
patched ethrex build.

Consequence: **one image per client per ceiling**. Five ceilings means ten image builds for
the whole campaign, reused across every role and `K_retry` cell.

```
images/build.sh 322800      # frame-tx-devnet/{nethermind,ethrex}:vg322800 + the generator
images/build.sh --all       # every campaign ceiling
```

Patches are applied to a copy inside the Docker build. The working tree is never modified.
Which of them are candidates for an actual PR against `eip8141-frame-txs-devnet7`, and which
must stay devnet-only, is tracked in [UPSTREAM-CANDIDATES.md](UPSTREAM-CANDIDATES.md).
Both patch scripts assert their anchor is unique first, so an upstream move fails the build
instead of silently producing an image that measures the wrong ceiling.

The Nethermind patch does one more thing: it maps `hezeTime` / `bogotaTime` onto EIP-8141
activation. Upstream schedules EIP-8141 on its own `eip8141PrototypeTime` key, which no
genesis generator emits. Without the mapping, Nethermind sits pre-fork on a devnet where
ethrex is already accepting frame transactions.

## K_retry

Neither client implements a retry counter, and EIP-8141 does not define one. `K_retry` is a
benchmark-side concept: the in-process harnesses emit `k_basis=modelled` and
`amplification_basis=closed_form`.

This devnet drives it from the transaction side instead, using the one real cross-client
analogue: the **EIP-8141 expiry deadline**. A transaction whose deadline is `K_retry` slots
ahead can be offered to at most that many block-building attempts before the pool must evict
it. `FrameTxPrefixRetryMeasurement` pins the behaviour: `deadline_slots=3` survives exactly 3
heads.

This is a modelling choice, not a client feature. It needs no patch and works identically on
both clients, but it bounds attempts by wall clock rather than by a counter, so a missed slot
costs an attempt.

## Topology

One Nethermind and one ethrex execution node, each with a Lighthouse beacon node and
validators, on the same chain. Fork schedule from ethrex's fixture:

```yaml
network_params:
  seconds_per_slot: 6
  fulu_fork_epoch: 0     # Osaka from genesis
  gloas_fork_epoch: 0
  heze_fork_epoch: 1     # frame-tx opcodes gate on Hegota, not Osaka
```

Frame transactions are invalid before epoch 1. The generator waits for the network and
records a `case=preflight` line per node before it times anything.

## Running

Prerequisites: Docker, and the [Kurtosis CLI](https://docs.kurtosis.com/install).

```bash
cd tools/frame-tx-kurtosis-devnet

# 1. Build the images one ceiling needs.
images/build.sh 322800

# 2a. The network plus a full scenario, by hand.
kurtosis run . --args-file scenarios/smoke.yaml --enclave frame-tx-smoke

# 2b. Or one scenario end to end, with collection and teardown.
runner/run_scenario.py --ceiling 322800 --attacker-role keccak-wide --k-retry 4

# 3. The campaign matrix: ceiling x role x K_retry, plus privacy inclusion per ceiling.
runner/run_matrix.py --standard --list      # 65 scenarios, see what would run
runner/run_matrix.py --standard
```

Start here on a fresh machine. The smoke test runs a short scenario and then asserts the
acceptance criteria against what was collected, so it fails loudly instead of reporting that
it ran:

```bash
runner/smoke_test.sh
```

It checks that both clients became ready, both advanced past one block, both rejected an
over-budget prefix, baseline frame transactions were included, the attacker role was
exercised and refused, and that metrics, events and provenance were all written.

Useful narrowing:

```bash
runner/run_matrix.py --ceilings 322800 500000 --roles keccak-wide --k-retries 1 8
runner/run_matrix.py --privacy-only --ceilings 100000 236285 300000 322800 500000
```

Teardown:

```bash
kurtosis enclave rm -f frame-tx-c322800-keccak-wide-k4-a25
kurtosis clean -a          # everything
```

`run_scenario.py` tears its own enclave down unless `--keep` is passed.

## Traffic roles

All roles submit round-robin to every execution node. An invalid frame transaction is
rejected at admission and never gossiped, so a node only pays for what is submitted to it
directly. Submitting to one node would measure one client.

| Role | Shape | Expected outcome |
|---|---|---|
| baseline | Self-verifying transfer, VERIFY frame with `flags=0x03` | Admitted and included |
| `keccak-wide` | VERIFY frame into a 4 KB `KECCAK256` loop sized to the ceiling | Rejected after burning the budget |
| `signature-stuffed` | secp256k1 entries padded to the ceiling, last one unverifiable | Rejected before the EVM runs |
| `soispoke-groth16` | Real verifier, real invalid proof | Rejected after the pairing completes |

The baseline runs during a warm-up phase on its own and then throughout the attack, so
degradation is measured against a live chain rather than an idle one. Both phases are
reported separately (`phase=warmup`, `phase=measured`).

Shapes are pinned to the in-process harness. `traffic/tests/test_shapes.py` asserts the
signature-stuffed shape reproduces the harness's own measured numbers exactly, for example
115 entries and 322400 declared gas at ceiling 322800.

```bash
python3 traffic/tests/test_shapes.py     # offline, no devnet needed
```

## Privacy inclusion

A separate probe, run inside the measured window, answering the campaign's utility question:
under this attack load, at this ceiling, does a valid privacy transaction still get admitted
and included?

It will not answer with a substitute. Real valid proof material has to be supplied:

```bash
runner/run_scenario.py --ceiling 322800 --privacy-inclusion \
  --groth16-artifacts <sweep-tree> ...
```

Without valid calldata it emits `case=privacy_inclusion available=no reason=...` and stops.
A synthetic gas-equivalent proof would make the run look successful while measuring nothing.

**Known gap:** the published `NethermindEth/frame-verify-gas` sweeps carry `verifier.hex` and
`calldata-invalid.hex` only. Every existing campaign harness measures rejection, so no valid
proof fixture ships today. The valid path needs material generated from
`soispoke/minimal-shielded-pool` (its `devnet/` tooling and committed proving key) or a
`calldata-valid.hex` added to the sweeps. Until then the attacker side of the Groth16 role
works and the inclusion probe reports itself unavailable.

## Observability

Primary source is native client telemetry. Both clients expose Prometheus metrics on port
9001 and `ethereum-package`'s Prometheus scrapes them automatically, labelled `service`,
`client_type` and `client_name`. Grafana ships with it.

```bash
kurtosis port print <enclave> grafana http
kurtosis port print <enclave> prometheus http
```

`run_scenario.py` writes both URLs into the results directory and snapshots the queries in
`runner/metrics_queries.json` over the run window into `results/<id>/metrics/*.json`. That
file is data-driven: edit it, nothing is hardcoded in the runner.

Nethermind already exposes the frame-transaction counters this campaign cares about, so they
need no generator-side equivalent:

- `PendingTransactionsFrameTxVerifyGasTooHigh`, `...VerifyStateGasTooHigh`,
  `...SignatureInvalid`, `...NoPayer`, `...Expired`
- `FrameTxSimulations`, `FrameTxSimulationsTimedOut`, `FrameTxSimulationsBusy`,
  `FrameTxSimulationsBudgetExhausted`
- `FrameTxRevalidations`, `FrameTxRevalidationsDeferred`, `FrameTxRevalidationEvictions`,
  `FrameTxExpiryShedEvictions`

**From the generator, not the clients:** submission and admission latency as the submitter
sees it, the rejection reason string per submission, inclusion latency and inclusion block for
a transaction the generator knows it sent, and the privacy-inclusion verdict. A client cannot
report the latency of a request it refused, nor the fate of a specific transaction hash.

Scenario, ceiling, attacker role and `K_retry` are attached as Kurtosis service labels on
every execution node, and repeated on every `RESULT` line.

## Results layout

```
results/<scenario-id>/
  <scenario-id>.result       RESULT key=value lines
  <scenario-id>.events.jsonl one record per submission and inclusion
  <scenario-id>.run.json     generator-side metadata
  run.json                   run provenance: params, commit, images, metric summary
  traffic.log                full generator log
  el-*.log                   per-node client logs
  metrics/*.json             Prometheus range queries over the run window
  grafana.url, prometheus.url
```

`run.json` carries the Nethermind commit, the image entries from `images/build-manifest.json`
(including the resolved ethrex commit), every scenario parameter and the topology file path.

`RESULT` cases: `scenario`, `preflight`, `ceiling_probe`, `fixture_deployed`, `admission`,
`inclusion`, `offered_load`, `head_progress`, `privacy_inclusion`, `scenario_complete`,
`scenario_aborted`.

## Verifying the ceiling is live

Before measuring, the generator submits a prefix deliberately just over the ceiling to each
node and records the verdict as `case=ceiling_probe`. A node running a stock image accepts
what a patched one refuses, so a mis-built image shows up as a probe line rather than as a
quietly wrong measurement.

## Known limitations

1. **No end-to-end enclave run has been executed yet.** Everything offline is validated:
   shell and Python syntax, the shape tests against the harness's own numbers, the args
   renderer and the matrix planner. The enclave path needs Kurtosis and a running Docker
   daemon, neither available where this was built. First run should be
   `kurtosis run . --args-file scenarios/smoke.yaml --enclave frame-tx-smoke`.
2. **`K_retry` is modelled as an expiry deadline**, not a client retry counter. See above.
3. **The valid privacy transaction fixture does not exist yet.** See above.
4. **Five ceilings means ten client images.** ethrex is a Rust build; budget time for the
   first build of each ceiling.
5. **The Nethermind image pins base image digests** copied from the repository `Dockerfile`.
   Re-sync them when that file moves.
6. **Both clients activate EIP-8141 with Hegota** on this devnet. Upstream Nethermind
   deliberately schedules it separately because the expiry-verifier predeploy shifts the
   EIP-7928 access list that Bogota consensus fixtures pin. Coupling them is correct here and
   wrong for those fixtures, which is why it lives in the devnet image and not in the tree.
7. **Published devnet images are stale.** `nethermindeth/nethermind:eip8141-frame-txs-devnet7`
   and `:frames-devnet-0` date from 2026-07-21, well behind the branch. Build from source.
8. **Attacker rate is offered, not achieved.** The generator records both; a saturated node
   will show them diverging, which is a measurement, not a failure.
