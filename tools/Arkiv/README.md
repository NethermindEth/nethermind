# Arkiv (GolemDB) for Nethermind

Runs Nethermind as an execution node of an [Arkiv](https://docs.arkiv.network) database chain — **Rabdi**
(chain id 7794095) and the other GolemDB networks published in
[db-chain-networks](https://github.com/Arkiv-Network/db-chain-networks) — following the chain from genesis with
Lighthouse, archive mode, and the `arkiv_*` RPC namespace.

The plugin is self-contained: it changes nothing in the rest of Nethermind, and is off unless `Arkiv.Enabled` is set
(`--config rabdi` sets it).

- [Architecture](docs/architecture.md) — what Rabdi is, where the plugin plugs in, the storage model.
- [Compatibility](docs/compatibility.md) — every difference from `arkiv-reth`, the reference client.
- [Sync and validation report](docs/sync-report.md) — the full archive sync from genesis and its checks.
- [Benchmark](docs/benchmark.md) — method and tooling against `arkiv-reth` v1.0.0; only a preliminary result so far.

| Deliverable | Where |
| --- | --- |
| Plugin and its tests | `src/Nethermind/Nethermind.Arkiv`, `src/Nethermind/Nethermind.Arkiv.Test` |
| Native shim (Rust, Arkiv's executor and GolemDB) | `src/native/arkiv-ffi` |
| Network files | `src/Nethermind/Chains/rabdi.json` (Rabdi's published genesis, also the chain spec); `src/Nethermind/Nethermind.Runner/configs/rabdi.json` (state sync), `rabdi_archive.json` (archive, from genesis) |
| Runnable setup | `tools/Arkiv/scripts/run-rabdi.sh` (Nethermind + Lighthouse, no Docker), `run-devnet.sh` |
| Compatibility report | [docs/compatibility.md](docs/compatibility.md) |
| Sync and validation report | [docs/sync-report.md](docs/sync-report.md) |
| Architecture | [docs/architecture.md](docs/architecture.md) |
| Validation and benchmark tools | `tools/Arkiv/tools/` |

## What Rabdi is, in one paragraph

Rabdi is **not** an OP Stack chain. It is a standalone post-merge chain — Lighthouse beacon chain (64 validators,
2 s slots, Electra at genesis) over an execution layer whose client is `arkiv-reth`
([Arkiv-Network/arkiv](https://github.com/Arkiv-Network/arkiv) v1.0.0 on a reth fork). That client runs **no EVM**:
a transaction is a plain transfer, a call to the entity engine at `0x4400…0044` (`execute(Operation[])`), or a
protocol `purgeExpired` the producer adds to every block; contract creation is refused. All state — balances,
nonces, entities and their query indexes — lives in **GolemDB** ([Arkiv-Network/golemdb](https://github.com/Arkiv-Network/golemdb)),
and the header's `stateRoot` is GolemDB's commitment, not a Merkle-Patricia root. `arkiv-op-geth`/`arkiv-op-reth` are
the older OP Stack product and are not what Rabdi runs.

## Why a native library

The state root is GolemDB's Merkle commitment over its cells, records and indexes, and gas is GolemDB's metering.
Re-implementing them in C# would mean porting roughly 100k lines of Rust byte-exactly and chasing every change
upstream. Instead `src/native/arkiv-ffi` exposes a small C ABI over the **same crates** the Rabdi client is built from,
pinned to the same tags, and the C# plugin calls it per block. Nethermind keeps everything Ethereum: blocks,
transactions, receipts, devp2p, the Engine API, the RPC server, the tx pool.

## Build

Requirements: .NET 11 SDK; for the native shim Rust 1.97 (`src/native/arkiv-ffi/rust-toolchain.toml`) and libclang.

```sh
# The Runner with the Arkiv plugin and its native library (cargo runs as part of the build).
dotnet build src/Nethermind/Nethermind.Runner -c Release -p:BuildArkivNative=true
RUNNER=src/Nethermind/artifacts/bin/Nethermind.Runner/release
```

Without `-p:BuildArkivNative=true` Nethermind builds as usual, with no Rust needed, and an Arkiv node fails at start
for want of `libarkiv_ffi.so`. A library already built in `src/native/arkiv-ffi/target/release` is picked up either way.

If bindgen cannot find `stdbool.h` (libclang installed without its resource headers), point
`BINDGEN_EXTRA_CLANG_ARGS` at a compiler's include directory, e.g. `-I/usr/lib/gcc/x86_64-linux-gnu/13/include`.

## Run a Rabdi node

Lighthouse v8.3.1 is the consensus client Rabdi's watchers run. With a checkout of db-chain-networks:

```sh
NETWORK_DIR=…/db-chain-networks/networks/testnet/rabdi \
NETHERMIND=$RUNNER LIGHTHOUSE=$(which lighthouse) tools/Arkiv/scripts/run-rabdi.sh /data/rabdi
```

Lighthouse can be the release binary. Built from source on a host without cmake, LevelDB can be left out:
`cargo build --release --bin lighthouse --no-default-features --features beacon-node-redb,slasher-redb` after setting
`default = ["redb"]` in `beacon_node/store/Cargo.toml` (and `database_manager`'s default backend to
`store::config::DEFAULT_BACKEND`). On a large shared volume Nethermind's percentage-based disk guard may refuse to
start; pass thresholds that fit through `EL_EXTRA_ARGS` (e.g. `--HealthChecks.LowStorageSpaceShutdownThreshold 1`).

It dials the watchers in `p2p.yaml` (devp2p for Nethermind, libp2p for Lighthouse), registers them as trusted beacon
peers, and syncs from genesis: every block is executed, nothing is restored from a snapshot.

## Configuration (`Arkiv.*`)

| Key | Default | |
| --- | --- | --- |
| `Enabled` | `false` | Turns the plugin on. |
| `GenesisFile` | `Init.ChainSpecPath` | The network's execution genesis (with `config.arkiv.golemdb`). Block 0 is built from it. Nethermind reads the same file as its chain spec; the plugin adds Arkiv's base-fee floor (the genesis base fee) to it. |
| `StoreDirectory` | `<db>/arkiv_golemdb` | The GolemDB store (its copies go to `<store>.checkpoints`). |
| `NativeLibrary` | beside the plugin | Path of `libarkiv_ffi.so`. |
| `EntityHistory` | `true` | Keep every entity after every block, so `arkiv_getEntity` answers at past blocks. |
| `CheckpointInterval` | `0` | Copy the GolemDB store every N blocks; a reorg below the store's head replays from the newest copy on the new branch. Both Rabdi configs set 50 000. |
| `HoldHeadBlock` | `true` | Commit a block only once a block builds on it, so a competing block at the tip (a late block replaced, a producer restart) executes over the same parent with no store copy ([arkiv#287](https://github.com/Arkiv-Network/arkiv/issues/287) stalls arkiv-reth there). `arkiv_*` reads and `eth_call` see the block before the head until the next block arrives. |
| `ServeState` | `false` | Serve a copy of the GolemDB store to peers that state-sync (`arkiv/1`). |
| `ServedCopyAge` | `64` | The fewest blocks between two served copies; a new one is made, at the head, when a peer needs a newer copy than the last. |

Two Rabdi configurations, both with `FlatDb.Enabled=false` (the plugin needs the patricia backend) and the watchers as
static peers:

| `--config` | Sync | History |
| --- | --- | --- |
| `rabdi` | sync from genesis, then follows the head; Nethermind's default pruning | from genesis on |
| `rabdi_archive` | every block from genesis | everything: accounts at every block (`Pruning.Mode=None`), every entity after every block (`EntityHistory`), the full transaction index |

### Prototype limits

Native RPC reads share the execution lock because a commit invalidates their native views. An expensive query can delay block import; use a separate RPC node for public queries. A safe timeout needs native query cancellation or independent snapshots and is still outstanding.

`CheckpointInterval=0` disables recovery below the committed store head; the bundled Rabdi configurations set 50,000. Operators must choose a checkpoint interval appropriate to their reorg and disk-copy budget.

Local account-trie roots are now indexed by block hash rather than by GolemDB root. Existing experimental plugin databases need replay or resync to rebuild historical local-root mappings.

### Joining by state sync

Peer-supplied native database files are not verified record by record against the state root. State sync is therefore disabled by default; enable `Arkiv.AllowUnverifiedStateSync=true` only with trusted peers. Otherwise the plugin disables fast/snap sync and executes from genesis. Serving a copy also requires `Arkiv.ServeState=true`. `Arkiv.MaxStateSyncBytes` bounds the total accepted manifest size (64 GiB by default), and downloads require enough free disk space.

With trusted peers, `--config rabdi --Arkiv.AllowUnverifiedStateSync=true` (`Sync.FastSync` and `Sync.SnapSync` on) fetches headers, and in place of snap's state phase fetches a
GolemDB store copy from a peer running this plugin, over the `arkiv/1` devp2p capability. It checks the copy's head against the header of its
block (block hash and GolemDB root) and builds its local account trie from the store's accounts. Then it executes blocks
from there on, as after any state sync. History before the sync point is not available on such a node. arkiv-reth
watchers do not speak `arkiv/1`, so at least one peer must run Nethermind with this plugin; a synced node serves only when `Arkiv.ServeState=true`, and the node uses whichever of its peers does.

Finding such a peer: Rabdi's own nodes run with discovery off and are reached through the enodes the network publishes
(the configs' static peers), so there is no discovery network to find a Nethermind node through yet. Until Nethermind
nodes for Rabdi are published (as bootnodes or static peers), add one with `--Network.StaticPeers`; without one the node
waits and says so, and `--config rabdi_archive` syncs from genesis instead.

## Tests

```sh
dotnet test --project src/Nethermind/Nethermind.Arkiv.Test -c Release -p:BuildArkivNative=true
(cd src/native/arkiv-ffi && cargo test --release)
```

The C# suite (34 tests) executes Rabdi blocks 1–700 over a GolemDB store on disk and checks every state root, receipts
root, gas figure and mirrored account against the headers and the store, plus restart, store-behind, reorg,
entity-history, transaction-processor and RPC-argument behaviour, and rebuilds all 700 purge transactions to their
hashes, and serves a store copy that a fresh node installs only at its own block (state sync). The native suite (8 tests) checks the live genesis, that a block reports every account it wrote, and that
building Rabdi's blocks chooses the same purge keys and reaches the same state roots, and that a store lists all its
accounts.

Block production against the reference client, on a devnet:

```sh
tools/Arkiv/scripts/run-devnet.sh /tmp/arkiv-devnet          # Nethermind + arkiv-reth on Arkiv's dev genesis (see the script)
(cd src/native/arkiv-ffi && cargo run --release --example devtx -- 1337 0 0 create 3)  # a signed tx for eth_sendRawTransaction
JWT=/tmp/arkiv-devnet/jwt.hex tools/Arkiv/tools/devnet_build.py 5      # build from the pool, import into both
```

Against the live network: `tools/Arkiv/tools/differential.py --node URL --vectors samples.jsonl` (see the sync report).
