# daisugi-devnet

Local fresh-genesis devnet in the Daisugi topology: one Nethermind, one Lighthouse beacon node, one Lighthouse validator client, plus optional replay and P2P sync nodes.

- Tooling: Docker Compose and shell scripts. No Kurtosis. Genesis comes from `ethpandaops/ethereum-genesis-generator`, pinned by digest.
- Isolated: compose project and Docker network `daisugi-devnet`, containers `daisugi-*`, host ports on `127.0.0.1` only.
- Disposable: every run directory holds its own mnemonic, validator keystores and JWT secret. Nothing secret is committed. Never reuse a run's keys elsewhere.
- Temporary home. Long-term home: AA-PQ, with pinned published images instead of local builds.

## Quick start

From a clone of this branch, on Linux x86_64 with the tools under [Prerequisites](#prerequisites):

1. Build both images: step 1 below.
2. Start a throwaway single-host chain and wait for finality past the fork (about 9 minutes):

   ```bash
   cd tools/daisugi-devnet
   export COMPOSE_FILE=docker-compose.yml:lighthouse-distroless.override.yml
   NM=daisugi/nethermind:0260e692fc LH=daisugi/lighthouse:eip8288-lean
   LOCAL=1 ./gen-genesis.sh run/devnet-1 $(( $(date +%s) + 120 )) 3 &&
     ./up.sh run/devnet-1 $NM $LH &&
     ./check.sh run/devnet-1
   ```

3. Send a proof-bearing transaction: step 4.
4. Stop: `GENESIS_DIR=$PWD/run/devnet-1 NETHERMIND_IMAGE=$NM LIGHTHOUSE_IMAGE=$LH DEVNET_USER=$(id -u):$(id -g) docker compose --profile replay --profile sync down && rm -rf run/devnet-1`.

To test your own consensus-layer changes, build your Lighthouse image and pass it as `LH`. The override clears the image entrypoint and finds `lighthouse` in `/usr/local/bin` or `/`, so it works for both the stock and the reproducible Lighthouse `Dockerfile`. `check.sh` then shows whether the chain still crosses the fork, produces the 25-field header and finalizes.

## Pinned sources

| Component | Source | Revision |
| --- | --- | --- |
| Nethermind | `NethermindEth/nethermind` `feature/eip8288-lean` (this branch's base) | `0260e692fcf8e6b8bcb41289d877e0d5e380b5cd` (interim pin) |
| Lighthouse | `sigp/lighthouse` v8.2.2 | `e423a66763bb1bd780492d635123f208d80c3538`, archive sha256 `3f3193218d8f7228889505eb508c6176ea60a964fcfb072eb248787c1e242d4c` |
| Lighthouse overlay | `asanso/AA-PQ` PR #10, `aggregation/frames/consensus/eip8288-lean` | `a03100abece8f8ed129c488db50ba89dde11b10a` |
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
  --build-arg COMMIT_HASH=0260e692fcf8e6b8bcb41289d877e0d5e380b5cd -t daisugi/nethermind:0260e692fc .
```

Lighthouse, from the pristine archive plus the AA-PQ overlay:

```bash
B=tools/daisugi-devnet/build; LH=e423a66763bb1bd780492d635123f208d80c3538; mkdir -p $B
curl -fsSL -o $B/lighthouse.tar.gz https://github.com/sigp/lighthouse/archive/$LH.tar.gz
echo "3f3193218d8f7228889505eb508c6176ea60a964fcfb072eb248787c1e242d4c  $B/lighthouse.tar.gz" | sha256sum -c
tar -xzf $B/lighthouse.tar.gz -C $B
git clone -q https://github.com/asanso/AA-PQ $B/AA-PQ
git -C $B/AA-PQ fetch -q origin a03100abece8f8ed129c488db50ba89dde11b10a && git -C $B/AA-PQ checkout -q FETCH_HEAD
(cd $B/AA-PQ/aggregation/frames/consensus/eip8288-lean && python3 -m unittest discover -p 'test_*.py' \
  && python3 apply.py lighthouse ../../../../../lighthouse-$LH --check && python3 apply.py lighthouse ../../../../../lighthouse-$LH)
docker build -f $B/lighthouse-$LH/Dockerfile.reproducible --platform linux/amd64 \
  --build-arg RUST_TARGET=x86_64-unknown-linux-gnu -t daisugi/lighthouse:eip8288-lean $B/lighthouse-$LH
```

- Expected Lighthouse binary sha256 (`/lighthouse` in the image): `2e709e64cb111070fb4846d98f928dffd0d646904574a07478ffcb02aa390bcb`.
- The reproducible image is distroless with `ENTRYPOINT /lighthouse`. The compose commands start with `lighthouse`, so every compose call needs the override below.

Set this in every shell that runs the scripts:

```bash
cd tools/daisugi-devnet
export COMPOSE_FILE=docker-compose.yml:lighthouse-distroless.override.yml
NM=daisugi/nethermind:0260e692fc LH=daisugi/lighthouse:eip8288-lean
```

### Native Lean fallback

- Nethermind at this pin has no NuGet package for `libnethermind_lean.so`, and the stock `Dockerfile` omits it. A node without it stops at `InitializeLeanBackend`.
- Until the `Nethermind.Crypto.Lean` package is pinned upstream: `images/nethermind.Dockerfile` builds the library with `cargo` in its `lean` stage, and test builds use `-p:BuildLeanFfi=true`.
- Once the package lands, build the stock `Dockerfile` instead and drop `images/nethermind.Dockerfile`. Rerun every step below against the new image.

## 2. Genesis and fork schedule

```bash
LOCAL=1 ./gen-genesis.sh run/devnet-1 $(( $(date +%s) + 120 )) 3
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
- `check.sh` waits for finality past the fork epoch (about 7 minutes after genesis). Expected output:

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
| `GENESIS_DIR`, `NETHERMIND_IMAGE`, `LIGHTHOUSE_IMAGE`, `DEVNET_USER`, `BEACON_MULTIADDR` | compose | set by the scripts; set them by hand for a manual `docker compose` call |

## Stop and clean up

```bash
GENESIS_DIR=$PWD/run/devnet-1 NETHERMIND_IMAGE=$NM LIGHTHOUSE_IMAGE=$LH DEVNET_USER=$(id -u):$(id -g) docker compose --profile replay --profile sync down
rm -rf run/devnet-1
```

## Known gaps

- Wallet, RPC gateway and explorer in AA-PQ still use the live chain's formats: wrapper `[dep, [witness]]` instead of `[[deps], [witnesses]]`, a 24-field header with the proof in it instead of 25 fields with the proof in the body, keccak instead of BLAKE2s dependency hash. The gateway also accepts only 7-field frame transactions while the wallet sends 8. None of them works against this devnet yet. This devnet tests clients only.
- Not configured: Osaka/Fulu (PeerDAS), Amsterdam, Gloas, Bogota, adaptive aggregation.
- Single host, Docker network only. No bootnodes, discovery, public ENRs or multi-host topology yet.
- arm64: no runtime-validated Lighthouse image (lock `binarySha256` for arm64 is `null`); Nethermind arm64 not run here.
- Nethermind pin is interim; the Lean library is built locally until the NuGet package exists.
- `proof.sh`, `sync.sh` and `sync-check.sh` read fixed container names and host ports, so they run only against the default `DEVNET_PREFIX`.
