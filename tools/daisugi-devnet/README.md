# daisugi-devnet

Local fresh-genesis devnet in the Daisugi topology: one Nethermind, one Lighthouse beacon node, one Lighthouse validator client, plus optional replay and P2P sync nodes.

- Tooling: Docker Compose and shell scripts. No Kurtosis. Genesis comes from `ethpandaops/ethereum-genesis-generator`, pinned by digest.
- Isolated: compose project and Docker network `daisugi-devnet`, containers `daisugi-*`, host ports on `127.0.0.1` only.
- Disposable: every run directory holds its own mnemonic, validator keystores and JWT secret. Nothing secret is committed. Never reuse a run's keys elsewhere.
- Temporary home. Long-term home: AA-PQ, with pinned published images instead of local builds.

## Pinned sources

| Component | Source | Revision |
| --- | --- | --- |
| Nethermind | `NethermindEth/nethermind` `feature/eip8288-lean` (this branch's base) | `1e5d1fa8a071b0b329a243437d00026e1d4a72df` (interim pin) |
| Lighthouse | `sigp/lighthouse` v8.2.2 | `e423a66763bb1bd780492d635123f208d80c3538`, archive sha256 `3f3193218d8f7228889505eb508c6176ea60a964fcfb072eb248787c1e242d4c` |
| Lighthouse patch series | `asanso/AA-PQ` PR #10, `aggregation/frames/consensus/eip8288-lean` | `f41267e01565bf39ea0c08b27cfeffed18c1822c` |
| Genesis generator | `ethpandaops/ethereum-genesis-generator` | `sha256:287f596b9a4ee72ae2aff194682f854650a8861542e489259adf0975cc0c9a54` |

## Prerequisites

- Linux x86_64 (the only validated platform), Docker with Compose 2.24 or later (`!reset`/`!override` tags).
- `jq`, `curl`, `openssl`, `python3`, `git`.
- For `proof.sh`: .NET SDK from `global.json` (10.0.300+) and Rust via `rustup` (`tools/lean-ffi/rust-toolchain.toml` selects the toolchain).

## 1. Build the images

From the repository root.

Nethermind, with the native Lean library built from `tools/lean-ffi`:

```bash
docker build -f tools/daisugi-devnet/images/nethermind.Dockerfile \
  --build-arg COMMIT_HASH=1e5d1fa8a071b0b329a243437d00026e1d4a72df -t daisugi/nethermind:1e5d1fa8a0 .
```

Lighthouse, from the pinned archive plus the AA-PQ patch series:

```bash
B=tools/daisugi-devnet/build; LH=e423a66763bb1bd780492d635123f208d80c3538; mkdir -p $B
curl -fsSL -o $B/lighthouse.tar.gz https://github.com/sigp/lighthouse/archive/$LH.tar.gz
echo "3f3193218d8f7228889505eb508c6176ea60a964fcfb072eb248787c1e242d4c  $B/lighthouse.tar.gz" | sha256sum -c
git clone -q https://github.com/asanso/AA-PQ $B/AA-PQ
git -C $B/AA-PQ fetch -q origin f41267e01565bf39ea0c08b27cfeffed18c1822c && git -C $B/AA-PQ checkout -q FETCH_HEAD
# apply.py checks the archive, each patch and the resulting tree against sources.lock.json.
python3 $B/AA-PQ/aggregation/frames/consensus/eip8288-lean/apply.py lighthouse $B/lighthouse.tar.gz $B/lighthouse
docker build -f $B/lighthouse/Dockerfile.reproducible --platform linux/amd64 \
  --build-arg RUST_TARGET=x86_64-unknown-linux-gnu -t daisugi/lighthouse:eip8288-lean $B/lighthouse
```

- Expected Lighthouse binary sha256 (`/lighthouse` in the image): `b249885cf90b06d772ce1eb94757e816c0910f514d200610f328991961d88eb2`.
- The reproducible image is distroless with `ENTRYPOINT /lighthouse`. The compose commands start with `lighthouse`, so every compose call needs the override below.

Set this in every shell that runs the scripts:

```bash
cd tools/daisugi-devnet
export COMPOSE_FILE=docker-compose.yml:lighthouse-distroless.override.yml
NM=daisugi/nethermind:1e5d1fa8a0 LH=daisugi/lighthouse:eip8288-lean
```

### Native Lean fallback

- Nethermind at this pin has no NuGet package for `libnethermind_lean.so`, and the stock `Dockerfile` omits it. A node without it stops at `InitializeLeanBackend`.
- Until the `Nethermind.Crypto.Lean` package is pinned upstream: `images/nethermind.Dockerfile` builds the library with `cargo` in its `lean` stage, and test builds use `-p:BuildLeanFfi=true`.
- Once the package lands, build the stock `Dockerfile` instead and drop `images/nethermind.Dockerfile`. Rerun every step below against the new image.

## 2. Genesis and fork schedule

```bash
LOCAL=1 ./gen-genesis.sh run/devnet-1 $(( $(date +%s) + 30 )) 3
```

- Arguments: output directory (must not exist), genesis timestamp, Daisugi fork epoch.
- `LOCAL=1` is a single-host throwaway run. A shared network runs without it, see "Shared network" below.
- Chain ID 1337 with `LOCAL=1`, 2-second slots, 32 slots per epoch, 64 validators, gas limit 60M, `MAX_PAYLOAD_SIZE` 20 MiB.
- EL: Prague and EIP-8141 (`eip8141PrototypeTime`) from genesis, EIP-8288 (`eip8288PrototypeTime`) at the fork boundary. Osaka, Amsterdam and Bogota removed.
- CL: Electra from genesis, `DAISUGI_FORK_VERSION 0xd0000038` at `DAISUGI_FORK_EPOCH`. Fulu and Gloas unscheduled.
- Writes `run/devnet-1/params.json` (schedule and EL genesis hash), `mnemonic`, `data/metadata/{genesis.json,genesis.ssz,config.yaml}`, `data/jwt/jwtsecret`, `data/validators/`.
- `data/metadata` holds no mnemonic: the generator's `mnemonics.yaml` is deleted.
- `DEVNET_MNEMONIC="<24 words>"` or `DEVNET_MNEMONIC_FILE` reuses a mnemonic. With the same mnemonic and timestamp, `genesis.json`, `genesis.ssz` and `config.yaml` are byte-identical.

## 3. Start and check

```bash
./up.sh run/devnet-1 $NM $LH
./check.sh run/devnet-1
```

| Service | Container | Host ports | Docker network ports |
| --- | --- | --- | --- |
| Nethermind | `daisugi-nethermind` | RPC `28545` | Engine `8551`, devp2p `30303` |
| Beacon node | `daisugi-beacon` | API `25052` | libp2p `9000` |
| Validator client | `daisugi-validator` | none | none |

- Peering: discovery off on both layers, no bootnodes (`boot_enr.yaml` is `[]`). Extra nodes join through static peers, see step 5.
- `check.sh` waits for finality past the fork epoch (about 4 minutes after genesis). Expected output:

```text
EL genesis hash 0x… matches the CL genesis state
Lean backend: InitializeLeanBackend did not fail
slot 95: electra, EL block 95 ts …, 21 header fields, starkProof null
slot 96: daisugi, EL block 96 ts …, 25 header fields, starkProof 0x
slots 96..…, EL blocks 96..…, missed 0
nethermind: 0 invalid/rejected/exception lines
PASS
```

A few beacon and validator `ERROR` lines at startup are expected: they connect before Nethermind or the beacon node listens.

### Shared network

Without `LOCAL=1`, `gen-genesis.sh` refuses to run unless all of these hold:

- `CHAIN_ID` is set and is not 1 or 1337.
- `DEVNET_MNEMONIC_FILE` (owned by you, mode 0600 or 0400) or `DEVNET_MNEMONIC` is set, and it is not a public test mnemonic.
- `OPERATORS` gives each operator a disjoint validator index range (`name:first:count`) and the ranges cover every validator.

```bash
CHAIN_ID=<new id> DEVNET_MNEMONIC_FILE=~/devnet.mnemonic OPERATORS='alice:0:32 bob:32:32' \
  ./gen-genesis.sh run/shared-1 <genesis timestamp> 3
```

- Each operator's keystores go to `data/operators/<name>`. Give each operator only `params.json`, `data/metadata` and their own directory. `up.sh` creates a separate JWT secret on each operator host, so `data/jwt` is not handed out. No two validator clients ever load the same key.
- No `mnemonic` file and no `config/values.env` are left in the output directory; a failed run deletes it. `eth2-val-tools` takes the mnemonic only as an argument, so run `gen-genesis.sh` on a host where no other user can list processes. Premined accounts derive from your mnemonic, so none has a publicly known key. `proof.sh send` reads `mnemonic`, so it runs only on `LOCAL=1` outputs.
- `up.sh` needs `OPERATOR=<name>` and `FEE_RECIPIENT=<address you control>`. It refuses the well-known fee recipients.
- `DEVNET_PREFIX`, `EL_RPC_PORT` and `CL_HTTP_PORT` change the container names, compose project, network and host ports. Pass the same values to `check.sh`.

## 4. Proof-bearing block

```bash
dotnet test --project ../../src/Nethermind/Nethermind.Crypto.LeanFfi.Test -c release -p:BuildLeanFfi=true
./proof.sh send run/devnet-1
```

- The test build writes the leanSig signer and fixtures (`lean-vectors`; leanSig key from seed `[42; 32]`, test-only).
- `send`: a frame transaction from the first premined account with a leanSig dependency (zero data hash, bound to the signature hash) and a leanSTARK dependency. Four corrupted wrappers go first, then the valid one through `eth_sendProofWrapper` (direct mode). `EL_RPC` picks another EL.
- Expected: four `rejected …` lines (three `a wrapper dependency proof failed verification`, one `SECP256K1 signer does not match`), then `valid wrapper: {… "result":["0x<tx hash>"]}`. Output: `run/devnet-1/proof/send.json`.

Find the proof block and verify it on an empty node:

```bash
TX=$(jq -r .txHash run/devnet-1/proof/send.json)
until curl -s -X POST -H 'content-type: application/json' --data "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"eth_getTransactionReceipt\",\"params\":[\"$TX\"]}" http://127.0.0.1:28545 | jq -ce '.result | select(. != null) | {blockNumber, status}'; do sleep 2; done
./proof.sh replay run/devnet-1 $NM <slot of the proof block>
```

- Before the fork the slot equals the block number. With no missed slots it still does; otherwise find the slot through `eth/v2/beacon/blocks`.
- `replay`: the `nethermind-replay` node (empty database, no peers, no CL, RPC `28546`, Engine `28551`) imports every canonical payload over the Engine API. At the proof slot it first sends a flipped, truncated or emptied proof, and the proof without its transaction.
- Expected: four `INVALID … InvalidRecursiveStark` lines, `proof <about 326000> B … VALID` for the proof block (the size varies slightly per run), `replayed slots 1..N, failures 0`.

## 5. P2P sync

```bash
./sync.sh run/devnet-1 $NM $LH genesis
./sync.sh run/devnet-1 $NM $LH checkpoint
./sync-check.sh <proof block number>
```

- Each pair starts empty and joins only through devp2p (static node: the original Nethermind's enode at its container IP, port 30303) and libp2p (`--libp2p-addresses`: the original beacon node's multiaddr, port 9000). `sync.sh` prints both.
- `genesis`: Lighthouse range-syncs from genesis. `checkpoint`: Lighthouse starts from the original beacon node's finalized state; its Nethermind fetches all earlier history over devp2p.
- Host ports: ELs `28547`, `28548`; beacon APIs `25053`, `25054`.
- Wait until both new beacon nodes reach the head (`curl -s 127.0.0.1:25053/eth/v1/node/syncing`), then run `sync-check.sh`. Expected: identical hash, state root and receipts root for every block on all three ELs; the same raw proof block and `starkProof` (about 326 KB) everywhere; the same finalized head; `PASS`.

## Kurtosis

`kurtosis/run.sh` starts the same single-node devnet as a Kurtosis enclave with [ethereum-package](https://github.com/ethpandaops/ethereum-package). It runs `check.sh` against the enclave, then sends `PQ_SERIES` PQ transaction series with `pq.sh`. The enclave is removed on exit, also when `kurtosis run` fails. `KEEP=1` keeps it after a successful `kurtosis run`.

```bash
tools/daisugi-devnet/kurtosis/run.sh daisugi
```

- Needs Kurtosis, the two images from step 1 (`daisugi/nethermind:1e5d1fa8a0`, `daisugi/lighthouse:eip8288-lean`), plus .NET SDK and Rust as for `proof.sh`.
- `network_params.yaml`: Nethermind and Lighthouse images, 64 validators, 2 s slots, chain ID 1337, Electra at genesis, the 8141 verifier and 8250 nonce manager predeploys, and `DAISUGI_FORK_EPOCH` 3.
- `genesis.Dockerfile` wraps the pinned genesis generator. After it runs, the wrapper sets `eip8141PrototypeTime` to genesis, sets `eip8288PrototypeTime` to the Daisugi fork slot, drops the Osaka/Amsterdam/Bogota times, appends `DAISUGI_FORK_VERSION` `0xd0000038`, and removes `SECONDS_PER_SLOT`. Lighthouse rejects `SECONDS_PER_SLOT` together with `SLOT_DURATION_MS`.
- `lighthouse.Dockerfile` adds `/bin/sh` to the distroless Lighthouse image and clears its `ENTRYPOINT`. ethereum-package starts the beacon node with `sh -c` itself, and the validator client runs `lighthouse vc ...` with the image entrypoint.
- `run.sh` writes `NETHERMIND_IMAGE` into a copy of `network_params.yaml` under `kurtosis/run/`. It finds ports and containers by the enclave's UUID and stops when any of them is missing or the beacon API is not up within 120 s.
- The PQ key defaults to the first account ethereum-package prefunds from its default mnemonic.
- Env: `PACKAGE` (pinned ethereum-package commit), `NETHERMIND_IMAGE`, `LIGHTHOUSE_IMAGE`, `PQ_SERIES` (`3`), `PQ_KEY`, `KEEP`.

### PQ transaction series

`pq.sh` builds the leanSig signer (`tools/lean-ffi` example `fixtures`, key from seed `[42; 32]`, test-only) and `pq-tx.cs`, which encodes frame transactions against this tree. One series, all through `eth_sendProofWrapper`:

1. A frame transaction with a leanSig dependency on nonce key 0. Its corrupted wrapper is rejected, the valid one is included.
2. The same on a random nonce key, sequence 0. Before it is sent, a sequence 1 transaction is rejected as `keyed nonce sequence not current`. After sequence 0 is included, sequence 1 is sent and included.
3. A byte-identical resend of sequence 0 is rejected as `already known`; a new transaction on sequence 0 as `keyed nonce sequence not current`.

- Every included receipt must have status `0x1`. Expected last line: `PASS pq: blocks <b0> <b1> <b2>`.
- Env: `PQ_KEY`, `CHAIN_ID`, `OUT` (fresh directory) are required; `PORT` (`8545`), `FX`, `TO`, `NONCE`, `NONCE_KEY`, `RECEIPT_TRIES` (`60`).

## Environment variables

| Variable | Used by | Default |
| --- | --- | --- |
| `COMPOSE_FILE` | every `docker compose` call | must include `lighthouse-distroless.override.yml` for the reproducible Lighthouse image |
| `LOCAL` | `gen-genesis.sh` | `0`; `1` allows chain ID 1337, a random or public mnemonic and one operator with all validators |
| `CHAIN_ID`, `OPERATORS`, `VALIDATORS` | `gen-genesis.sh` | required without `LOCAL=1`; `VALIDATORS` is `64` |
| `DEVNET_MNEMONIC`, `DEVNET_MNEMONIC_FILE` | `gen-genesis.sh` | random per run with `LOCAL=1`; required otherwise |
| `OPERATOR`, `FEE_RECIPIENT` | `up.sh` | required on a shared network |
| `DEVNET_PREFIX`, `EL_RPC_PORT`, `CL_HTTP_PORT` | compose, `check.sh` | `daisugi`, `28545`, `25052` |
| `EL_RPC` | `proof.sh send` | `http://127.0.0.1:28545` |
| `BLOBS_SUPPORT` | compose, Nethermind `TxPoolConfig.BlobsSupport` | `StorageWithReorgs`; `Disabled` lets the node negotiate eth/73 before PeerDAS |
| `GENESIS_DIR`, `NETHERMIND_IMAGE`, `LIGHTHOUSE_IMAGE`, `DEVNET_USER`, `BEACON_MULTIADDR` | compose | set by the scripts |

## Stop and clean up

```bash
docker compose --profile replay --profile sync down
rm -rf run/devnet-1
```

## Known gaps

- Wallet, RPC gateway and explorer in AA-PQ still use the live chain's formats: wrapper `[dep, [witness]]` instead of `[[deps], [witnesses]]`, a 24-field header with the proof in it instead of 25 fields with the proof in the body, keccak instead of BLAKE2s dependency hash. The gateway also accepts only 7-field frame transactions while the wallet sends 8. None of them works against this devnet yet. This devnet tests clients only.
- Not configured: Osaka/Fulu (PeerDAS), Amsterdam, Gloas, Bogota, adaptive aggregation.
- Single host, Docker network only. No bootnodes, discovery, public ENRs or multi-host topology yet.
- arm64: no runtime-validated Lighthouse image (lock `binarySha256` for arm64 is `null`); Nethermind arm64 not run here.
- Nethermind pin is interim; the Lean library is built locally until the NuGet package exists.
- `proof.sh`, `sync.sh` and `sync-check.sh` read fixed container names and host ports, so they run only against the default `DEVNET_PREFIX`.
