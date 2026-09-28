# frame-tx-kurtosis-devnet

Local Kurtosis devnet for the EIP-8141 `MAX_VERIFY_GAS` campaign. It runs the campaign's attack
shapes against real nodes: gossip, mempool admission, block production and import, with a
consensus client driving slots.

It measures **behaviour** (does an honest frame transaction still land under attack, what do
the clients' own metrics show). It does not produce `R_max`: the campaign takes capacity from
the single-core in-process harnesses on the `reproducible-benchmarks` runner.

## Status

| Item | State |
|---|---|
| Profile | soispoke v2 (`position-notes-v2`, `6dedda19`): 235,800 declared |
| Ceilings | 100,000 · 235,800 · 250,000 · 300,000 · 400,000 · 500,000 |
| Topology | Two Nethermind nodes, each with Lighthouse. ethrex is excluded, see [Limitations](#limitations) |
| Results before 2026-09-19 | Withdrawn, see [CORRECTIONS.md](CORRECTIONS.md) |
| v1 results (322,800) | Historical, in CORRECTIONS.md. No v2 devnet results yet |

## Prerequisites

| Tool | macOS (Apple Silicon) | Linux |
|---|---|---|
| Docker | Docker Desktop, at least 8 CPUs, 16 GB memory, 40 GB disk | Docker Engine |
| Kurtosis CLI | `brew install kurtosis-tech/tap/kurtosis-cli` | [docs.kurtosis.com/install](https://docs.kurtosis.com/install) |
| Python | System `python3` (runner uses the standard library only) | same |
| GitHub CLI | `brew install gh`, for the Groth16 release | same |

The traffic generator and its tests run inside a Docker image, so the host needs no Python
packages.

Kurtosis's engine listens on port 8081. Free it before starting (see
[Troubleshooting](#troubleshooting)).

## Quick start

From the repository root, on this branch:

```bash
cd tools/frame-tx-kurtosis-devnet

# 1. Nethermind at the v2 ceiling, plus the traffic generator. About 10 minutes the first time.
images/build.sh 235800

# 2. Short end-to-end run with assertions: both nodes ready, blocks produced, EIP-8141 active,
#    ceiling enforced, baseline frame transactions included, attacker refused.
runner/smoke_test.sh --no-build
```

The smoke test prints one `[ok  ]` or `[FAIL]` line per check and exits non-zero on any
failure. Raw output lands in `results/smoke-c235800/`.

## Groth16 artifacts

The `soispoke-groth16` role deploys a real Groth16 verifier and floods it with invalid proofs.
It needs the v2 sweeps from `NethermindEth/frame-verify-gas`:

```bash
mkdir -p ~/frame-verify-gas-v2 && cd ~/frame-verify-gas-v2
gh release download v2.0.0 --repo NethermindEth/frame-verify-gas
shasum -a 256 -c SHA256SUMS
for t in sweep-*.tar.gz; do tar xzf "$t"; done
```

Each `sweep-*/` directory holds `verifier.hex` and `calldata-invalid.hex`. Kurtosis can only
upload files inside this package, so the runner copies those files into the gitignored `groth16/`
before each run. The role picks the
sweep by ceiling: `sweep-soispoke` at 100,000 and 235,800, and the matching control at 250,000,
300,000, 400,000 and 500,000.

## Running scenarios

One scenario, end to end (render args, start the enclave, run traffic, collect, tear down):

```bash
runner/run_scenario.py --ceiling 235800 --attacker-role signature-stuffed --k-retry 1 \
  --attacker-rate 25 --groth16-artifacts ~/frame-verify-gas-v2
```

Useful flags: `--keep` leaves the enclave up, `--dry-run` prints the rendered args,
`--split-traffic` aims the attack at the first node and honest traffic at the second, to separate
local damage from damage to the chain.

The matrix (ceiling × role × `K_retry`, plus a privacy probe per ceiling):

```bash
runner/run_matrix.py --standard --list          # 78 scenarios; prints the plan only
runner/run_matrix.py --ceilings 235800 500000 --roles signature-stuffed --k-retries 1 \
  --groth16-artifacts ~/frame-verify-gas-v2
```

Each ceiling needs its own image: `images/build.sh <ceiling>`, or `images/build.sh --all`.

`kurtosis run .` uploads this whole folder, gitignored files included. For a long matrix pass
`--results-dir` outside the package, for example `--results-dir ~/frame-tx-devnet-results`, so the
upload does not grow with every run.

Teardown if a run was interrupted:

```bash
kurtosis enclave rm -f <enclave>
kurtosis clean -a
```

## How it works

```
scenarios/base.yaml ──► runner/run_scenario.py ──► kurtosis run .
                          renders frame_tx block        │
                                                        ▼
                                             main.star (this package)
                                   ┌────────────────────┼──────────────────┐
                                   ▼                    ▼                  ▼
                       ethereum-package run()   src/scenario.star   src/traffic.star
                       Nethermind + Lighthouse  image + ceiling     traffic generator
                       Prometheus, Grafana,     flag per node       service
                       Dora
```

- `ethereum-package` is imported, not forked, and pinned in `kurtosis.yml` to `c0db06b2`.
- The generator (`traffic/`) builds frame transactions with soispoke's encoder, fetched at
  image build time from `6dedda19` and checked against a SHA-256 pin.

### The ceiling

`Eip8141Constants.MaxVerifyGas` is a compile-time constant. `--TxPool.FrameTxMaxVerifyGas`
bounds only the declared-gas check; simulated prefixes and signature verification stay at the
constant. So each ceiling is its own image:

- `images/build.sh` exports `HEAD` with `git archive`, applies `images/nethermind/patch.sh`
  and builds the repository `Dockerfile`. Uncommitted changes are never in the image.
- The runner also sets the runtime flag to the same value.
- Before timing anything, the generator sends a prefix just over the ceiling to each node and
  records the verdict as `case=ceiling_probe`. A stock image accepts what a patched one refuses.

### EIP-8141 activation

The genesis stays on Fulu, as public devnets do. The patched image reads
`NETHERMIND_EIP8141_ACTIVATION_OFFSET_SECONDS` and activates EIP-8141 that long after genesis
(120 s by default). The generator records `case=fork_gate` once each node accepts frame
transactions.

### K_retry

Neither EIP-8141 nor either client has a retry counter. The devnet expresses `K_retry` as an
expiry deadline `K_retry` slots ahead: a pending transaction can meet at most that many build
attempts before the pool must evict it. A missed slot costs an attempt.

## Traffic roles

Every role submits round-robin to all execution nodes: an invalid frame transaction is refused
at admission and never gossiped.

| Role | Shape | Expected outcome |
|---|---|---|
| baseline | Self-verifying transfer | Admitted and included |
| `keccak-wide` | VERIFY frame into a 4 KB `KECCAK256` loop sized to the ceiling | Refused after burning the budget |
| `signature-stuffed` | `floor((C - 400) / 2800)` secp256k1 entries, last one wrong | Refused before the EVM runs |
| `soispoke-groth16` | Real v2 verifier, invalid proof | Refused after the pairing |

The baseline runs alone during warm-up and then throughout the attack; both phases are
reported (`phase=warmup`, `phase=measured`). Declared gas follows Nethermind's
`FrameTxValidation.ValidationWorkGas`.

## Results

```
results/<scenario-id>/
  <scenario-id>.result        RESULT key=value lines
  <scenario-id>.events.jsonl  one record per submission and inclusion
  run.json                    provenance: parameters, commit, images
  traffic.log, el-*.log       generator and client logs
  metrics/*.json              Prometheus range queries over the run window
```

`RESULT` cases: `scenario`, `preflight`, `fork_gate`, `ceiling_probe`, `fixture_deployed`,
`admission`, `inclusion`, `offered_load`, `head_progress`, `privacy_inclusion`,
`scenario_complete`, `scenario_aborted`. The generator reports offered and achieved rate
separately; a gap between them is a measurement, not a failure.

Grafana and Prometheus URLs are written to the results directory. Nethermind's frame-transaction
counters (`PendingTransactionsFrameTxVerifyGasTooHigh`, `FrameTxSimulations*`,
`FrameTxRevalidation*`) are scraped automatically; the queries live in
`runner/metrics_queries.json`.

## Tests

| Check | Command | Needs |
|---|---|---|
| Shapes, prefix gas, watcher | `docker run --rm --entrypoint python frame-tx-devnet/traffic:local tests/test_shapes.py` (and `tests/test_watcher.py`) | Traffic image |
| Nethermind patch still applies and compiles | `images/nethermind/check-patch.sh` | .NET SDK |
| Matrix planner and args renderer | `runner/run_matrix.py --standard --list`, `runner/run_scenario.py --dry-run` | Python |
| End to end | `runner/smoke_test.sh` | Docker, Kurtosis |

## Limitations

1. **ethrex cannot join.** ethrex `main` still decodes a frame as
   `[mode, flags, target, gas_limit, value, data]` with flat fee fields. Nethermind reads
   `limits = [execution, state]` and a nested `fees` list, the envelope soispoke v2 encodes. The
   two cannot share a chain until ethrex moves. `images/build.sh <ceiling> --only ethrex` still
   builds it for when that happens. See [UPSTREAM-CANDIDATES.md](UPSTREAM-CANDIDATES.md).
2. **No valid privacy transaction.** The sweeps carry invalid proofs only, so the
   privacy-inclusion probe reports `available=no` instead of substituting a synthetic proof.
3. **One host.** All nodes share the host's CPUs; the devnet has spare capacity a home staker
   would not. Do not read capacity numbers from it.
4. **`K_retry` is modelled** as an expiry deadline, not a client counter.
5. **Offered rate is not achieved rate** when the generator or a node saturates. Both are
   recorded.

## Troubleshooting

| Symptom | Cause | Fix |
|---|---|---|
| `Bind for 0.0.0.0:8081 failed: port is already allocated` | Another container holds the Kurtosis engine port | Stop it: `docker ps --format '{{.Names}} {{.Ports}}' \| grep 8081` |
| `Expected exactly one network matching the name ... 'bridge', but got 2` | Docker Swarm is active and created `docker_gwbridge` | `docker swarm leave --force`, then `docker network rm docker_gwbridge` |
| `patch.sh`: `expected 1 occurrence of ...` | An anchor moved upstream | Update `images/nethermind/patch.sh`, confirm with `check-patch.sh` |
| `frametx.py checksum mismatch` | The soispoke pin was changed without re-pinning the hash | Review the encoder diff, then update `traffic/vendor/fetch.sh` |
| Baseline transactions never included | EIP-8141 not active yet | Wait for `case=fork_gate ... active=yes`, about 120 s after genesis |

## History

- [CORRECTIONS.md](CORRECTIONS.md): the withdrawn pre-2026-09-19 results, the generator fixes,
  and the v1 re-measurement at 322,800.
- [UPSTREAM-CANDIDATES.md](UPSTREAM-CANDIDATES.md): the Nethermind changes this devnet patches
  in, and which of them belong upstream.
