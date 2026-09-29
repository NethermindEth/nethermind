# frame-tx-kurtosis-devnet

A local two-client devnet for the EIP-8141 `MAX_VERIFY_GAS` campaign. One Nethermind and one
ethrex node, each with a Lighthouse beacon node, run the fork schedule of the public
frames-devnet-0: Amsterdam from genesis, frame transactions from `bogotaTime`. A traffic
generator sends honest frame transactions and one attack shape, then the runner checks what
each client did.

## What a run shows, and what it does not

A passing run shows, for each client and at the ceiling built into its image:

- EIP-8141 activates and both clients stay on the same chain.
- A validation prefix declared just over the ceiling is refused.
- Every attack transaction is refused at admission, after the expensive work ran (the prefix
  simulation or the signature recoveries), not on a cheap early check.
- Every honest frame transaction is included, and each client builds blocks carrying frame
  transactions that the other imports.

It does not show:

- Capacity or `R_max`. The load is light (a few percent of a core per client) and everything
  shares one host. Capacity comes from the single-core harnesses in PR #13914 on the
  `reproducible-benchmarks` runner.
- Attack traffic over devp2p. Attacks enter through JSON-RPC; an invalid frame transaction is
  never gossiped.
- Transactions that are admitted and fail later, or retry behaviour (`K_retry`).
- A valid privacy transfer. No valid proof ships with the Groth16 sweeps.

## Requirements

| | macOS (Apple Silicon) | Linux |
|---|---|---|
| Docker | Docker Desktop, 8 CPUs, 16 GB memory, 60 GB disk | Docker Engine |
| Kurtosis CLI | `brew install kurtosis-tech/tap/kurtosis-cli` | [docs.kurtosis.com/install](https://docs.kurtosis.com/install) |
| Python 3 | System `python3` | System `python3` |
| GitHub CLI | `brew install gh` (for the Groth16 sweeps) | same |

The host scripts use bash 3.2, `python3` and no GNU-only tools. The traffic generator and its
tests run in a Docker image. The Kurtosis engine needs port 8081 free.

## Quick start

From the repository root, on branch `test/frame-tx-kurtosis-devnet`:

```bash
cd tools/frame-tx-kurtosis-devnet
images/build.sh 235800
runner/smoke_test.sh --no-build
```

`images/build.sh` builds the Nethermind and ethrex images for one ceiling, plus the traffic
generator. The first ethrex build compiles Rust and takes 15 to 30 minutes; later builds reuse
the cache. The smoke test waits for EIP-8141 to activate (about 3 minutes after genesis), runs
24 seconds of honest traffic alone, then 2 minutes of keccak-wide attack at 5 tx/s alongside
it. It prints one line per check and exits non-zero if any check fails. A run takes about
10 minutes.

## Groth16 sweeps

The `soispoke-groth16` role deploys a real Groth16 verifier and sends it invalid proofs. It
needs the `v2.0.0` sweeps from `NethermindEth/frame-verify-gas`:

```bash
mkdir -p ~/frame-verify-gas-v2 && cd ~/frame-verify-gas-v2
gh release download v2.0.0 --repo NethermindEth/frame-verify-gas
shasum -a 256 -c SHA256SUMS
for t in sweep-*.tar.gz; do tar xzf "$t"; done
```

At 100,000 and 235,800 the role uses `sweep-soispoke`, the real v2 verifier. At 250,000,
300,000, 400,000 and 500,000 it uses the synthetic control of that size.

## Running scenarios

One attack shape, with checks:

```bash
runner/smoke_test.sh --no-build --role signature-stuffed --rate 25
runner/smoke_test.sh --no-build --role soispoke-groth16 --rate 25 --groth16-artifacts ~/frame-verify-gas-v2
runner/smoke_test.sh --no-build --ceiling 500000 --role signature-stuffed --rate 10
```

A ceiling other than 235,800 needs its images first: `images/build.sh 500000`.

Several scenarios, each followed by the same checks:

```bash
runner/run_matrix.py --list --standard            # 18 scenarios: 6 ceilings x 3 roles
runner/run_matrix.py --ceilings 235800 500000 --roles signature-stuffed --attacker-rate 10
```

`run_matrix.py` writes to `~/frame-tx-devnet-results` by default. `kurtosis run .` uploads the
whole package folder, so results kept inside it would be uploaded again by every later
scenario.

Useful `runner/run_scenario.py` options: `--keep` leaves the enclave up for inspection,
`--dry-run` prints the rendered Kurtosis arguments, `--split-traffic` sends the attack to the
first node only and the honest traffic to the second, `--nethermind-image` or `--ethrex-image`
replaces a ceiling image.

If a run is interrupted:

```bash
kurtosis enclave ls
kurtosis enclave rm -f <enclave>
```

## Reading the results

Each scenario writes `results/<scenario-id>/`:

| File | Content |
|---|---|
| `<scenario-id>.result` | One `RESULT key=value` line per measurement |
| `<scenario-id>.events.jsonl` | One record per submission and per inclusion |
| `run.json` | Parameters, images and their labels, checkout commit |
| `el-*.log`, `traffic.log` | Full client and generator logs |
| `metrics/*.json` | Prometheus range queries over the run |

`runner/check_results.py results/<scenario-id> <role>` re-runs the checks on saved results.

The rows most worth reading:

| Case | Fields |
|---|---|
| `admission` | per node and role: `samples`, `accepted`, `rejected`, `submit_p50_us`, `submit_p95_us`, `top_reject_reason`. The submit latency of the attack role is the cost of one refusal as that client's JSON-RPC caller sees it. |
| `inclusion` | honest traffic: `submitted`, `included`, `outstanding`, `starved`, inclusion latency |
| `ceiling_probe` | the verdict on a prefix declared just over the ceiling |
| `image_ceiling` | the ceiling label of the image each container ran |
| `chain_agreement` | one block's hash on every client |
| `block_builders` | blocks and frame transactions per building client |
| `generator_capacity` | whether the generator can build the offered rate; the run aborts if not |

## How it works

| Piece | Pin | Where |
|---|---|---|
| Network, genesis, consensus layer, Prometheus, Grafana | `ethpandaops/ethereum-package` `c0db06b2` | `kurtosis.yml`, `scenarios/base.yaml` |
| Consensus client | `ethpandaops/lighthouse:unstable-4b1f3c2` | `scenarios/base.yaml` |
| Nethermind | this checkout (`NETHERMIND_REF`, default `HEAD`) | `images/build.sh`, `images/nethermind/patch.sh` |
| ethrex | `lambdaclass/ethrex` `52c2e626` (branch `frames-devnet-0`) | `images/build.sh`, `images/ethrex/patch.sh` |
| Frame-transaction encoder | `soispoke/minimal-shielded-pool` `6dedda19`, SHA-256 pinned | `traffic/vendor/fetch.sh` |

**The ceiling.** Both clients compile `MAX_VERIFY_GAS` in. Each ceiling is therefore its own
pair of images, tagged `vg<ceiling>` and labelled with the value. Nethermind also takes the
runtime flag `--TxPool.FrameTxMaxVerifyGas`, which the scenario sets to the same value: the flag
bounds the declared gas at admission, while the constant caps the simulated prefix. The
`image_ceiling` check confirms each container ran the image built for the scenario's ceiling.

**Activation.** `heze_fork_epoch: 1` with `frames_enabled: true` writes `bogotaTime` into the
execution genesis and keeps the consensus layer off Heze. Both clients read `bogotaTime` as
Amsterdam plus EIP-8141. Nethermind's upstream code maps it to its Bogota fork (with EIP-7805),
which the consensus layer does not speak, so `patch.sh` routes it to EIP-8141 alone, as the
upstream frames-devnet-0 deployment does.

**Nonces.** ethrex simulates a frame transaction at admission only if its nonce is the sender's
state nonce. Attack transactions all carry the attacker's state nonce (they are never included,
so it never moves). Honest traffic rotates across the remaining prefunded accounts with one
transaction in flight per account.

## Tests

| Check | Command | Needs |
|---|---|---|
| Shapes, prefix gas, nonce rotation, chain watcher | `for t in shapes accounts watcher; do docker run --rm --entrypoint python frame-tx-devnet/traffic:local tests/test_$t.py; done` | traffic image |
| Nethermind patch applies and compiles | `images/nethermind/check-patch.sh` | .NET SDK |
| Planner and argument rendering | `runner/run_matrix.py --list --standard`, `runner/run_scenario.py --dry-run` | Python |
| End to end | `runner/smoke_test.sh` | Docker, Kurtosis |

## Verified runs

Run on 2026-09-28 on one Linux x86_64 host (32 cores, Docker 29.7.2, Kurtosis 1.20.0). Images:
Nethermind from `b3ee425d` (the images carry `8286cdba`, the same tree before a commit-message
edit), ethrex `52c2e626`. Every run passed every check in
`runner/check_results.py`. Refusal latency is the attack role's `submit_p50_us`; the honest
baseline's is about 0.8 ms on both clients. Not yet run on macOS.

| Ceiling | Attack, rate | Nethermind: refused, p50 | ethrex: refused, p50 | Honest included | Frame txs in blocks built by Nethermind / ethrex |
|---|---|---|---|---|---|
| 235,800 | keccak-wide, 5/s | 300/300, 3.0 ms | 300/300, 3.2 ms | 146/146 | 81 / 66 |
| 235,800 | signature-stuffed, 25/s | 1501/1501, 2.8 ms | 1500/1500, 2.9 ms | 146/146 | 66 / 82 |
| 235,800 | soispoke-groth16 (`v2.0.0`), 25/s | 1501/1501, 2.4 ms | 1500/1500, 2.8 ms | 146/146 | 50 / 97 |
| 500,000 | signature-stuffed, 10/s | 601/601, 5.3 ms | 600/600, 5.5 ms | 146/146 | 85 / 63 |
| 500,000 | keccak-wide, 5/s | 301/301, 5.5 ms | 300/300, 5.2 ms | 145/145 | 78 / 68 |

Refusal reasons, per client:

| Attack | Nethermind | ethrex |
|---|---|---|
| keccak-wide | `validation prefix frame reverted` | `validation prefix frame reverted` |
| signature-stuffed | `SECP256K1 signer does not match the recovered address` | `Invalid frame transaction signature` |
| soispoke-groth16 | `validation prefix never set a payer` | `validation prefix did not establish a payer` |

**The compiled ceiling is live.** The 500,000 keccak-wide run was repeated with Nethermind on
`vg300000`, an image whose compiled `MaxVerifyGas` is the stock 300,000, and the runtime flag
still at 500,000:

| Nethermind image | Refusal p50 | Reason |
|---|---|---|
| `vg500000` | 5.5 ms | `validation prefix frame reverted` (the loop ran its 497k budget) |
| `vg300000` | 3.5 ms | `validation prefix exceeds MAX_VERIFY_GAS` (simulation capped at 300k) |

The runtime flag alone cannot raise the ceiling, and the `image_ceiling` check fails that run, as
it should.

## Troubleshooting

| Symptom | Fix |
|---|---|
| `Bind for 0.0.0.0:8081 failed: port is already allocated` | Stop the container on 8081: `docker ps --format '{{.Names}} {{.Ports}}' \| grep 8081` |
| `Expected exactly one network matching the name ... 'bridge', but got 2` | Docker Swarm is on: `docker swarm leave --force`, then `docker network rm docker_gwbridge` |
| `scenario_aborted ... generator cannot build the offered rate` | Lower `--rate` or give Docker more CPUs. It is a generator limit, not a client result. |
| `patch.sh: expected 1 occurrence of ...` | Upstream moved the anchor. Update the patch and run `images/nethermind/check-patch.sh`. |
| `frametx.py checksum mismatch` | The encoder pin changed. Review the encoder diff, then update `traffic/vendor/fetch.sh`. |

## History

- [UPSTREAM-CANDIDATES.md](UPSTREAM-CANDIDATES.md): the client changes this devnet needs, and
  which of them belong upstream.
- [CORRECTIONS.md](CORRECTIONS.md): the withdrawn September 2026 v1 results and the generator
  fixes behind them.
