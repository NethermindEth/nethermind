# Architecture

## 1. Rabdi as deployed (established from source and the live chain)

| | Fact | Source |
| --- | --- | --- |
| Network record | chain id / network id 7794095, public RPC `rpc.rabdi.db-chain.testnet.arkiv.network` | `db-chain-networks/networks/testnet/rabdi/metadata.yaml` |
| Execution client | `ghcr.io/arkiv-network/arkiv-reth:v1.0.0` (digest-pinned) — `Arkiv-Network/arkiv` tag v1.0.0 (827a83b), reth fork `custom-golemdb-v2.7.0-v1.0.0` (a91d851), golemdb v1.0.0 (aa20dbe) | `rabdi/images.env`; `arkiv/Cargo.toml` at v1.0.0; `web3_clientVersion` = `reth/v2.7.0-a91d851` |
| Consensus | Lighthouse v8.3.1, 64 genesis validators, `SLOT_DURATION_MS: 2000`, every fork through Electra at epoch 0 (Fulu not scheduled) | `rabdi/config.yaml`, `images.env` |
| Execution forks | Frontier…Prague all at genesis (block 0 / timestamp 0), TTD 0 | `rabdi/genesis.json` |
| Genesis | 2026-10-30 18:40:17 UTC; block 0 `0xa65a2b9e…cbce`, `stateRoot` `0x18f6f205…a9ec` (GolemDB's) | public RPC, reproduced by this plugin |
| Peering | 10 watcher execution enodes (port 30002) and 10 beacon ENRs, discovery off, trusted peers | `rabdi/p2p.yaml`, `run-node.sh` |
| Joining | `run-node.sh`: arkiv-reth + Lighthouse; restores a published snapshot unless `FROM_GENESIS=1` | `rabdi/README.md` |

**Public data.** The public RPC serves every historical block, transaction and receipt, but state (balances,
nonces, entities, queries) at the head only, and its `eth_getLogs` fails (`golemdb segment_range_of(transactions):
NotImplemented`). Peer discovery is off network-wide: the 10 watchers' enodes and ENRs in `p2p.yaml` are the way in,
and they serve blocks over devp2p (`eth/71`) and libp2p. Execution+consensus snapshots are published at
`testnet.snapshots.arkiv.network` (not used here). Lighthouse's genesis sync from the watchers ran at 60–100 slots/s.

**Not OP Stack.** There is no L1, no derivation pipeline, no sequencer feed: Lighthouse drives the execution client
over the Engine API like any post-merge Ethereum chain. `arkiv-op-geth` and `arkiv-op-reth` are the earlier OP
Stack product (last changed July 2026) and play no part in Rabdi.

### The execution client's differences from upstream reth

`arkiv-reth` is reth's Ethereum node with these substitutions (`arkiv/bin/arkiv-reth/src/node.rs`,
`crates/arkiv-reth-executor/src/lib.rs`):

1. **No EVM.** `ArkivEvm::transact_raw` replaces revm for user transactions: a call to `0x4400…0044` runs the entity
   engine (`execute(Operation[])`, the `entityNonce`/`customAttributeNames`/`attributeTypeId` views, the protocol-only
   `purgeExpired`), anything else is a plain transfer (21 000 gas, floored at EIP-7623's calldata cost), and `CREATE`
   is a typed invalid transaction. System calls (EIP-4788, 2935, 7002, 7251) still go to revm.
2. **Fees.** EIP-1559 as revm applies it, except that the base-fee share of an entity transaction's *storage* gas
   is credited to the pool account `0x4400…0044` instead of burned. Gas for entity operations is GolemDB's metering
   priced by the genesis' `config.arkiv.golemdb.metering` weights (`arkiv-tokenomics`).
3. **Base-fee floor.** `next_base_fee = max(eip1559(parent), genesis base fee)` (`arkiv-reth-chainspec`), 1 gwei on
   Rabdi. This is why every Rabdi block has base fee exactly 1 gwei.
4. **State and state root.** Balances, nonces, entities, the query index and minting nonces all live in GolemDB.
   The header's `stateRoot` is GolemDB's commitment (`ArkivStateRootStrategy`); reth's Merkle stages are disabled.
5. **Protocol purge.** The producer adds one `purgeExpired(bytes32[])` per block, from a fixed sender with
   `nonce == block number`, `gasLimit == 1 000 000`, `maxFeePerGas == 2^128−1`; it skips sender checks and fees and
   deletes up to 10 expired entities.
6. **Storage of blocks.** Headers, transactions and receipts go to GolemDB's immutable-data segments, outside the
   state root; reth's own database is never written after genesis.
7. **No rewind.** GolemDB commits are final; the store reads at its head only (v1.0.0), so the node serves state at
   the head only ("arkiv serves state at the chain head only … (see #145)").
8. **RPC.** `arkiv_query`, `arkiv_getEntity`, `arkiv_getEntityCount`, `arkiv_debugEntityExists`,
   `arkiv_getBlockTiming`.

### GolemDB

GolemDB is a cell-oriented, Merkleised, historised database on MDBX: records of typed cells, a bitmap/term index
over indexed cells, a branch-only Merkle trie committing cells and index terms, immutable-data segments for
append-only rows, branches with frames (checkpoint/rollback) for transaction atomicity, and seal/commit for block
atomicity. **It is integrated into Rabdi** — it *is* Rabdi's state (`arkiv/crates/arkiv-golemdb`,
`arkiv-golemdb-state`) — and its root is consensus. It does not use Ethereum's state trie. Entities are records keyed
by entity key; accounts are records keyed by address with `$balance`/`$nonce` cells; expiry is the `$expiresAt`
cell, enforced by the purge transaction and filtered at read time; the Merkle commitment is computed at seal.
Determinism requirements: identical genesis settings (cell/record limits, hash function, metering weights, segment
schemas — all part of the root), identical operation order, identical metering.

## 2. Where the plugin plugs in

```
                Lighthouse ──Engine API──▶ Nethermind (unchanged core)
                                            │  devp2p (eth/71) ◀──▶ arkiv-reth watchers
                                            │  BlockchainProcessor / BranchProcessor
                                            ▼
                    ┌───────────── ArkivBlockProcessor (subclass of BlockProcessor) ─────────────┐
                    │ ProcessOne: ArkivChain.Execute(block) ──P/Invoke──▶ libarkiv_ffi            │
                    │                                   reth's executor + ArkivEvmFactory        │
                    │                                   over GolemDB; commit iff root == header  │
                    │ system calls ─▶ EVM (mirror state)                                           │
                    │ each user tx ─▶ ArkivTransactionProcessor: takes its native receipt          │
                    │ FinalizeBlock: mirror accounts into the trie, header.StateRoot = GolemDB's  │
                    │ producing: purge tx first, then pool txs one by one through a native build  │
                    │   session (taken or refused by Arkiv's executor); root of the sealed branch │
                    └────────────────────────────────────────────────────────────────────────────┘
```

| Concern | Mechanism | Core change? |
| --- | --- | --- |
| Block execution | `IBlockProcessor` → `ArkivBlockProcessor` (DI, scoped) | no |
| Transaction execution, eth_call, estimateGas | `ITransactionProcessor` decorator | no |
| Header state root | `FinalizeBlock` override; `IGenesisBuilder` decorator for block 0 | no |
| Opening state by header | `IWorldStateManager` and `IStateHeaderProvider` decorators translate GolemDB root → local trie root | no |
| Base-fee floor | `Eip1559BaseFeeMinValue` (existing Nethermind feature), set from the genesis base fee when the chain spec (Rabdi's genesis file) leaves it out | no |
| Arkiv RPC | `IArkivRpcModule` (`arkiv` namespace) | no |
| State sync | `IStateSyncRunner` → `ArkivStateSyncRunner`: a peer's GolemDB store over the `arkiv/1` satellite capability (`ArkivProtocolHandler`, `IP2PCapabilityResolver`), checked against the header, account trie rebuilt from it | no |
| Block production | `ArkivBlockProcessor` on a `BlockToProduce` (purge prepended, native build session); `IBlockProductionTransactionPicker` → `ArkivTransactionPicker` (nonce, balance and fees left to Arkiv's executor) | no |
| Storage | `arkiv` RocksDB (keyed DB via `AddDatabase`) + the GolemDB store directory | no |
| Consensus, sync, peers | Nethermind's merge sync, devp2p, Engine API as they are | no |
| Precompiles / opcodes | none: Arkiv runs no EVM for users | — |

## 3. Storage model

Three stores, one per kind of data:

1. **GolemDB** (`<db>/arkiv_golemdb`): Arkiv's consensus state at the head, exactly as `arkiv-reth` keeps it, committed
   block by block and tagged with the block hash. The plugin never writes it except through Arkiv's own executor.
2. **Nethermind's databases**: blocks, receipts, the tx index, and an **account trie** (archive, patricia). The trie is
   a mirror: after each block, the balance and nonce of every account the block wrote are read back from the
   committed GolemDB store and set in the trie; system-contract state comes from the system calls Nethermind runs.
   Its root is local — it is not in any header — and serves `eth_getBalance`, `eth_getTransactionCount` and
   `eth_getProof` at any past block.
3. **The `arkiv` database**: (a) GolemDB root → local trie root, which is how a header is opened as state; (b) each
   committed block's native result under its hash; (c) the entity archive: per entity key, the blocks that touched it
   and its `arkiv_getEntity` JSON after each.

### Keeping GolemDB and Nethermind in step

GolemDB has no rewind and opens a block only over its head. `ArkivChain` handles every way the two can diverge:

| Situation | Handling |
| --- | --- |
| Block's parent is the store's head | execute; commit iff the root matches the header |
| Block already committed (restart that lost Nethermind's last blocks, a retried branch) | replay its cached result |
| Store behind (parent is a descendant of the head) | execute the missing blocks from Nethermind's block tree |
| Reorg below the head | restore the newest store copy on the new branch's ancestry, replay forward |
| Wrong state root | `InvalidBlockException`; nothing committed |
| Read-only processing (tracing, simulation) | executed on a branch that is discarded |

## 4. Choices considered

| Option | Verdict |
| --- | --- |
| Reimplement GolemDB + executor in C# | Rejected: ~100k lines to keep byte-identical (Merkle encoding, metering, query engine) with every upstream release. |
| Run arkiv-reth as a side service and copy its results | Rejected: that is depending on another client's execution. |
| GolemDB as a separate local service | Unneeded: the store is a library with an in-process API; a process boundary adds failure modes (the Arkiv team notes the same for their A/B harness). |
| **Native FFI to Arkiv's own crates, pinned to the deployed tags** | **Chosen**: the state transition is the network's by construction; Nethermind executes every block itself. |
| Nethermind core changes | Not needed: every seam already exists as a DI service. |
