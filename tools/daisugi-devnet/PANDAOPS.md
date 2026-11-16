# Daisugi restart: handover to EthPandaOps

EthPandaOps generates the shared genesis with their own Kurtosis / ethereum-package setup. This file lists what that setup needs to run the restart stack, and how it differs from a stock ethereum-package network. We hand over images, parameters and checks, never a genesis or keys.

## What is custom, and why

| Piece | Stock ethereum-package | Daisugi needs | Why |
| --- | --- | --- | --- |
| EL image | upstream Nethermind | `nethermind` built from `9065d9a50b5036f9d1b08b650234acb0a1d4823e` (see "Images") | EIP-8141 frame txs, EIP-8288 proofs, EIP-8437 wrapper relay, F128 native backend |
| EL flags | none | `--TxPool.FrameTxMaxVerifyGas=500000`, `--TxPool.BlobsSupport=Disabled` | Frame verification budget; without blob support peers negotiate eth/73 and relay proof wrappers before PeerDAS |
| CL image | upstream Lighthouse | Lighthouse v8.2.2 + AA-PQ#10 patches, plus `/bin/sh` | The Daisugi fork (Electra plus the EIP-8288 proof in the payload); the reproducible image is distroless, and ethereum-package starts the beacon node through `sh` |
| EL genesis | generator output | `config.eip8141PrototypeTime` = genesis timestamp, `config.eip8288PrototypeTime` = Daisugi fork time, no `osakaTime`, `amsterdamTime`, `bogotaTime` | The generator knows neither prototype fork |
| CL config | generator output | `DAISUGI_FORK_VERSION: 0xd0000038`, `DAISUGI_FORK_EPOCH: <epoch>`; no `SECONDS_PER_SLOT` | The generator knows no Daisugi fork; Lighthouse refuses `SECONDS_PER_SLOT` next to `SLOT_DURATION_MS` |
| Predeploys | none | the two contracts in `additional_preloaded_contracts` below | EIP-8141 expiry verifier and EIP-8250 nonce manager, used by frame transactions |
| Generator image | `ethpandaops/ethereum-genesis-generator` | the same at `sha256:287f596b…`, plus `/apps/validator-mapping` from `sha256:e05851fc…` | The pinned ethereum-package calls `validator-mapping`, which the pinned generator lacks |

Everything else is stock: mainnet preset, Electra at genesis, Fulu, Gloas and BPOs unscheduled, prefunded accounts and validators from the network's own mnemonic.

The two genesis edits are the only custom genesis logic. `kurtosis/genesis.Dockerfile` and `kurtosis/daisugi-genesis.sh` apply them inside ethereum-package. EthPandaOps can use that image or apply the same edits in their own pipeline:

```bash
# EL: after the generator wrote /data/metadata/genesis.json
T8141=$(jq -r .timestamp genesis.json)
T8288=$(( GENESIS_TIME + DAISUGI_FORK_EPOCH * 32 * SLOT_DURATION_MS / 1000 ))   # GENESIS_TIME = CL genesis time
jq --argjson a $((T8141)) --argjson b $T8288 '.config.eip8141PrototypeTime=$a | .config.eip8288PrototypeTime=$b
  | del(.config.osakaTime, .config.amsterdamTime, .config.bogotaTime)' genesis.json > g && mv g genesis.json
# CL
sed -i '/^SECONDS_PER_SLOT:/d' config.yaml
printf '\nDAISUGI_FORK_VERSION: 0xd0000038\nDAISUGI_FORK_EPOCH: %s\n' "$DAISUGI_FORK_EPOCH" >> config.yaml
```

Nethermind reads `genesis.json` (ethereum-package passes `--Init.ChainSpecPath=/network-configs/genesis.json`); `chainspec.json` and `besu.json` are not used and need no edit.

## Tested parameters

From `kurtosis/network_params.yaml`, validated on 2026-11-16 (3 participants):

```yaml
participants:
  - el_type: nethermind
    el_image: <nethermind image>
    el_extra_params:
      - --TxPool.FrameTxMaxVerifyGas=500000
      - --TxPool.BlobsSupport=Disabled
    cl_type: lighthouse
    cl_image: <lighthouse image with /bin/sh>
    vc_type: lighthouse
    vc_image: <lighthouse image with /bin/sh>
    count: 1
    supernode: false
    validator_count: 64
network_params:
  network_id: "<new chain id>"           # tested: 1337 (Kurtosis), 7337 (shared-mode compose)
  preset: mainnet
  seconds_per_slot: 2
  genesis_delay: 20
  genesis_gaslimit: 60000000
  preregistered_validator_keys_mnemonic: "<EthPandaOps-held mnemonic>"
  electra_fork_epoch: 0
  fulu_fork_epoch: 18446744073709551615
  gloas_fork_epoch: 18446744073709551615
  bpo_1_epoch: 18446744073709551615      # bpo_2..bpo_5 likewise
  max_payload_size: 20971520
  additional_preloaded_contracts:
    "0x81413f0cF12e9b6a49B1D0439E081c577D57FfFf":
      balance: "0"
      code: "0x60083614600a575f5ffd5b5f3560c01c4211601657005b5f5ffd"
      storage: {}
      nonce: 1
    "0x8250968C12e01A19d6F667b9B2F3b3A4d0e51cB7":
      balance: "0"
      storage: {}
      nonce: 1
ethereum_genesis_generator_params:
  image: <generator image with daisugi-genesis.sh>
  extra_env:
    DAISUGI_FORK_EPOCH: "3"              # tested: 1 and 3; 0 not tested
```

- ethereum-package tested at `5795b77e65235ad223d4d3b413b2739190bf60cc`. A newer revision may change the generator interface or launcher flags; rerun the checks below if it is used.
- Validator layout: any split works; finality needs more than 2/3 of the stake online and peered. Tested 3 × 64 (Kurtosis) and 2 × 32 (two separate networks, finality only with both halves).
- Every EL must be Nethermind from the pinned commit with blob support off; every CL this Lighthouse build.
- Slot time 2 s, gas limit 60M and a 20 MiB payload cap are what was tested. Longer slots are expected to work but were not run.

## Images

```bash
# Nethermind: stock Dockerfile; the native F128 backend comes from Nethermind.Crypto.Lean 0.21.0.110, no Rust
git clone https://github.com/NethermindEth/nethermind nm && git -C nm checkout 9065d9a50b5036f9d1b08b650234acb0a1d4823e
docker build --build-arg COMMIT_HASH=9065d9a50b5036f9d1b08b650234acb0a1d4823e -t <registry>/nethermind:daisugi-9065d9a50b nm
# Lighthouse: README step 1 builds daisugi/lighthouse:eip8288-lean from the pinned archive and patches; then add /bin/sh
docker build --build-arg LIGHTHOUSE_IMAGE=daisugi/lighthouse:eip8288-lean -f kurtosis/lighthouse.Dockerfile \
  -t <registry>/lighthouse:daisugi-eip8288-lean kurtosis
# Genesis generator with the Daisugi edits
docker build -f kurtosis/genesis.Dockerfile -t <registry>/ethereum-genesis-generator:daisugi-287f596b kurtosis
```

Expected inside the images: `/nethermind/libnethermind_lean.so` sha256 `aad07d8fa0f106457ef1719eadd1685a98e39fe6df3ab27308022e14637b8f00` (x64); `/lighthouse` sha256 `109c9a91e128db8cb17be6939c5f614b1ae494a68da53814e9300bc5299ccd3d`. `nethermind --version` prints `Commit: 9065d9a5…`. Publish them and pass `image@sha256:…` references.

## Checks on the generated genesis

Before distributing it:

- `genesis.json`: `config.eip8141PrototypeTime` equals `timestamp`; `config.eip8288PrototypeTime` equals the CL genesis time plus `DAISUGI_FORK_EPOCH × 32 × slot seconds`; no `osakaTime`, `amsterdamTime`, `bogotaTime`.
- `config.yaml`: `DAISUGI_FORK_VERSION: 0xd0000038`, the agreed `DAISUGI_FORK_EPOCH`, `SLOT_DURATION_MS` present, `SECONDS_PER_SLOT` absent; `FULU_FORK_EPOCH` far future.
- The two predeploys in `alloc`, the 8141 one with the code above.
- After start: the EL genesis hash equals `latest_execution_payload_header.block_hash` of the CL genesis state (`check.sh` asserts it), and Nethermind logs no `InitializeLeanBackend ... failed`.

## Checks on the running network

From `tools/daisugi-devnet` (see [RESTART.md](RESTART.md)):

- `check.sh`: finality past the fork, slot before the fork `electra` and fork slot `daisugi` with `eip8288PrototypeTime`, 0 missed slots after the fork.
- `pq.sh` with `FX` from the 0.21.0.110 package and a funded key, for `PQ_SCHEME` `sphincs`, `stark` and `mixed`.
- Wrapper relay: a wrapper sent to one node shows up on the others (`eth_getTransactionByHash`) within seconds. If not, a node runs with blob support on.
- `sync-check.sh` against several nodes, including one synced from genesis.

## Reproduce our run

```bash
cd tools/daisugi-devnet/kurtosis
NODES=3 KEEP=1 PQ_SERIES=1 PQ_SCHEMES="sphincs stark mixed" FX=<fixtures from the 0.21.0.110 nupkg> \
  NETHERMIND_IMAGE=<nethermind image> LIGHTHOUSE_IMAGE=<lighthouse image without /bin/sh> ./run.sh daisugi
```

`run.sh` builds the `/bin/sh` wrapper and the generator image itself, runs ethereum-package at the pinned commit, then `check.sh` and the PQ series.
