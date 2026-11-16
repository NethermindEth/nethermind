# Daisugi restart: EthPandaOps handover

EthPandaOps generates the genesis with its own Kurtosis / ethereum-package setup. This file lists what that setup needs for Daisugi. We provide images, configuration and checks, not a genesis and no keys.

## 1. What Daisugi needs

- **EL**: Nethermind built from the pinned commit, every node, with `--TxPool.FrameTxMaxVerifyGas=500000` and `--TxPool.BlobsSupport=Disabled`. Without the second flag, PQ transactions do not propagate between nodes.
- **CL**: Lighthouse with the Daisugi fork patches. The release image is distroless; [`kurtosis/lighthouse.Dockerfile`](kurtosis/lighthouse.Dockerfile) adds busybox as `/bin/sh`, which ethereum-package needs to start the beacon node. Use that wrapped image for `cl_image` and `vc_image`.
- **Genesis**: two predeploys and two post-generation edits (section 3). This is the only custom genesis logic.
- **Generator**: the pinned ethereum-package calls `/apps/validator-mapping/merge.sh`, which the pinned generator image lacks; `kurtosis/genesis.Dockerfile` copies it from a later generator image and adds the genesis edits.
- Everything else is stock: mainnet preset, Electra at genesis, Fulu, Gloas and BPOs unscheduled.

## 2. Kurtosis configuration

Tested file: [`kurtosis/network_params.yaml`](kurtosis/network_params.yaml), with ethereum-package `5795b77e65235ad223d4d3b413b2739190bf60cc`.

| Setting | Tested | EthPandaOps chooses |
| --- | --- | --- |
| `participants` | 3 × (Nethermind + Lighthouse + 64 validators); the file holds one entry with `count: 1`, `run.sh` sets `count` from `NODES` | participants and validator split; finality needs more than 2/3 of the stake online |
| `el_extra_params` | `--TxPool.FrameTxMaxVerifyGas=500000`, `--TxPool.BlobsSupport=Disabled` | keep both |
| `network_id` | 1337 | a new chain ID |
| `preregistered_validator_keys_mnemonic` | the public ethereum-package test mnemonic | a private mnemonic |
| `seconds_per_slot` | 2 | keep 2 (others not tested) |
| `genesis_gaslimit`, `max_payload_size` | 60000000, 20971520 | keep |
| `electra_fork_epoch` | 0 | keep |
| `fulu_fork_epoch`, `gloas_fork_epoch`, `bpo_1..5_epoch` | 18446744073709551615 | keep |
| `additional_preloaded_contracts` | the two predeploys of section 3 | keep exactly |
| `ethereum_genesis_generator_params.extra_env.DAISUGI_FORK_EPOCH` | 3 | the Daisugi fork epoch; 1 tested outside ethereum-package, 0 not tested |

## 3. Genesis customization

Predeploys, nonce 1, balance 0, empty storage, as the EIPs require:

| Address | Code | Source |
| --- | --- | --- |
| `0x81413f0cF12e9b6a49B1D0439E081c577D57FfFf` | `0x60083614600a575f5ffd5b5f3560c01c4211601657005b5f5ffd` | EIP-8141 expiry verifier |
| `0x8250968C12e01A19d6F667b9B2F3b3A4d0e51cB7` | `0x60006000fd` | EIP-8250 `NONCE_MANAGER` |

Post-generation edits, applied by [`kurtosis/daisugi-genesis.sh`](kurtosis/daisugi-genesis.sh) inside the generator image:

- EL `genesis.json`: `config.eip8141PrototypeTime` = the EL genesis `timestamp`; `config.eip8288PrototypeTime` = `MIN_GENESIS_TIME + GENESIS_DELAY + DAISUGI_FORK_EPOCH × 32 × SLOT_DURATION_MS / 1000`, all read from the generated `config.yaml` (the generator folds ethereum-package's `genesis_delay` into `MIN_GENESIS_TIME` and writes `GENESIS_DELAY: 0`, so do not add `genesis_delay` again); `osakaTime`, `amsterdamTime`, `bogotaTime` removed. Nethermind loads this file (`--Init.ChainSpecPath=/network-configs/genesis.json`); `chainspec.json` and `besu.json` are unused.
- CL `config.yaml`: `DAISUGI_FORK_VERSION: 0xd0000038` and `DAISUGI_FORK_EPOCH` appended; `SECONDS_PER_SLOT` removed (Lighthouse refuses it next to `SLOT_DURATION_MS`).

The script fails instead of writing a shifted fork time when `DAISUGI_FORK_EPOCH`, `MIN_GENESIS_TIME`, `GENESIS_DELAY` or `SLOT_DURATION_MS` is missing, the preset is not mainnet, or the slot is not whole seconds.

## 4. Images and versions

| Image | Source | Required property |
| --- | --- | --- |
| Nethermind | `NethermindEth/nethermind` `af50ed19171295395ea12513f6d9464b782f4c5a` (`feature/eip8288-lean`), stock `Dockerfile`, `--build-arg COMMIT_HASH=<that sha>` | `/nethermind/libnethermind_lean.so` sha256 `aad07d8fa0f106457ef1719eadd1685a98e39fe6df3ab27308022e14637b8f00` (x64); `--version` prints that commit |
| Lighthouse | v8.2.2 `e423a66763bb1bd780492d635123f208d80c3538` + `asanso/AA-PQ` `aggregation/frames/consensus/eip8288-lean` patches at `4b0a11e0e7d261bf47a2e78589bcdff050703412`, built as in [README step 1](README.md#1-build-the-images), then wrapped by [`kurtosis/lighthouse.Dockerfile`](kurtosis/lighthouse.Dockerfile) | `/lighthouse` sha256 `109c9a91e128db8cb17be6939c5f614b1ae494a68da53814e9300bc5299ccd3d` in both the base and the wrapped image; `sh` present in the wrapped one |
| Genesis generator | [`kurtosis/genesis.Dockerfile`](kurtosis/genesis.Dockerfile): `ethpandaops/ethereum-genesis-generator@sha256:287f596b…` + `/apps/validator-mapping` from `@sha256:e05851fc…` + `daisugi-genesis.sh` | |

- Native backend: `Nethermind.Crypto.Lean` 0.21.0.110, nupkg `https://apiint.nugettest.org/v3-flatcontainer/nethermind.crypto.lean/0.21.0.110/nethermind.crypto.lean.0.21.0.110.nupkg`, sha256 `daecd150e646cdbc1ecaffb0fa118dff780c8a928a2ff705a7c3362fddd51ac2`. The Nethermind build restores it; no Rust.
- No image is published yet. Nethermind publishes the three images (Nethermind, wrapped Lighthouse, generator) and sends `image@sha256:…` references before the genesis is generated. Until then each image is reproducible from the sources above.
- Tested on linux-x64 only.

## 5. Validation

On the generated files, before start:

```bash
jq '.config | {eip8141PrototypeTime, eip8288PrototypeTime, osakaTime}' genesis.json   # osakaTime null
jq '.alloc["0x8250968C12e01A19d6F667b9B2F3b3A4d0e51cB7"].code' genesis.json        # "0x60006000fd"
grep -E '^(DAISUGI_FORK|SECONDS_PER_SLOT|SLOT_DURATION_MS)' config.yaml             # no SECONDS_PER_SLOT
```

On the running network, from this directory (`kurtosis/run.sh` runs all of this locally: `NODES=3 KEEP=1 PQ_SCHEMES="sphincs stark mixed" FX=<fixtures> NETHERMIND_IMAGE=<image> LIGHTHOUSE_IMAGE=<base image> kurtosis/run.sh <enclave>`; it refuses images whose pinned binaries differ; without `KEEP=1` the enclave is removed when it ends):

- `check.sh`: EL and CL genesis agree, Lean backend up, finality past the fork, fork boundary at `eip8288PrototypeTime`, no missed slots after the fork. It inspects one node (node 1 under `run.sh`); run it against others by setting `EL_RPC_PORT`, `CL_HTTP_PORT` and the container variables.
- `pq.sh` for `PQ_SCHEME=sphincs`, `stark` and `mixed`: valid wrappers included, invalid ones rejected. Needs the .NET SDK 10.0.300+, `FX` = `testing/runtimes/linux-x64/native/fixtures` from the nupkg above (sha256 `fc25affec68cbafacd35a728a7f665bd6aa75cbe0e59b7fc0775d4f47386b982`) and `PQ_KEY`, a key funded in this genesis (the `run.sh` default is funded only by the public test mnemonic).
- Propagation: build one wrapper, send it to node A, and look it up on node B before it is included:

  ```bash
  dotnet run -c release --file pq-tx.cs -- "$FX" <chain id> - 0 0x000000000000000000000000000000000000dEaD 123 /tmp/w 4242 <<<"$PQ_KEY"
  printf '{"jsonrpc":"2.0","id":1,"method":"eth_sendProofWrapper","params":["%s"]}' "$(cat /tmp/w.wrapper.hex)" \
    | curl -s -H 'content-type: application/json' --data-binary @- http://<node A rpc>
  curl -s -H 'content-type: application/json' --data "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"eth_getTransactionByHash\",\"params\":[\"$(cat /tmp/w.txhash)\"]}" http://<node B rpc>
  ```

  Node B returns the transaction within about a second.
- `sync-check.sh` across nodes, including one synced from genesis: identical blocks, proofs and finalized head.
- Optional, wallet: AA-PQ `docs/LOCAL-DEVNET.md` (asanso/AA-PQ#13, not merged yet) deploys the lean factory and sends a wallet transaction.

Local runs need a Docker host that is not a Swarm node and has port 8081 free (Kurtosis engine).

## 6. Not tested

`additional_services` (Dora, spamoor and others), checkpoint sync, ethereum-package or generator revisions other than the pinned ones, slot times other than 2 s, arm64. Block explorers and other tooling were not tried against the Daisugi fork (fork version `0xd0000038`).

Agree with Nethermind on the Daisugi fork epoch and the chain ID before generating the genesis.
