# Compatibility with arkiv-reth (the Rabdi reference client)

Reference: `arkiv-reth` v1.0.0 as Rabdi deploys it (`ghcr.io/arkiv-network/arkiv-reth:v1.0.0`, the public RPC
`rpc.rabdi.db-chain.testnet.arkiv.network`, `web3_clientVersion` `reth/v2.7.0-a91d851`). Every row below was checked
against that RPC or the reference binary built from the same tag; the checks are in `tools/differential.py` and
the [sync report](sync-report.md) (27 474 comparisons, 0 mismatches).

## Consensus: identical by construction, verified on the whole chain

| | |
| --- | --- |
| Genesis | block 0 `0xa65a2b9e…cbce`, `stateRoot` `0x18f6f205…a9ec` — reproduced from the published genesis file |
| State transition | Arkiv's own executor and GolemDB, the crates of arkiv-reth v1.0.0 at the same commits |
| State root, receipts root, gas used, logs bloom, requests hash | checked by Nethermind against every header of the chain |
| Base fee | Arkiv's floor (`max(eip1559, genesis base fee)`) through Nethermind's `Eip1559BaseFeeMinValue` |
| Header validation, Engine API (V4 payloads, Prague), devp2p | Nethermind's, unchanged; peers with the watchers over `eth/71` |

## RPC

| Method | arkiv-reth | Nethermind + plugin |
| --- | --- | --- |
| `eth_getBlockBy*`, `eth_getTransaction*`, `eth_getTransactionReceipt` | ✓ | identical (consensus fields; see presentation fields below) |
| `eth_getLogs` | **fails**: `-32603 golemdb segment_range_of(transactions): NotImplemented` | works; checked against the logs in the reference's receipts |
| `eth_getBalance`, `eth_getTransactionCount` at the head | ✓ | identical |
| … at a past block | refused: "arkiv serves state at the chain head only" | answered (archive account trie), checked against values the reference reported when that block was its head |
| `eth_getProof` | method not available | answered, against the **local account trie** (its root is not in the header; GolemDB proofs have no RPC yet in either client) |
| `eth_call`, `eth_estimateGas` | Arkiv executor at the head | the same executor at the head; a `block` argument does not change the state used, as on the reference |
| `eth_getCode`, `eth_getStorageAt` | `0x` / zero for every address (the reference's state provider holds no code or storage) | user accounts: identical. The six genesis predeploys (deposit contract, EIP-4788/2935/7002/7251 contracts, CREATE2 factory) return their genesis code, and EIP-4788/2935 storage carries the system calls' writes: Nethermind keeps the state system calls run on, reth discards it. Not consensus on Arkiv. |
| `eth_sendRawTransaction` | ✓ | a replayed user transaction is refused (`already known`; the reference says `nonce too low`), the protocol purge too (zero balance; the reference: fee cap). New transactions (entity creates, transfers, a contract creation) accepted into the pool on the devnet below |
| `debug_trace*` | method not available | refused with "Arkiv transactions run no EVM and cannot be traced": a tracer re-runs transactions one at a time, off their block, which an Arkiv transaction is not |
| `arkiv_getEntity` at the head | ✓ | identical (Arkiv's own read code) |
| `arkiv_getEntity` at a past block | `-32006 state for this block is not retained` | answered from the node's entity archive (`Arkiv.EntityHistory`) |
| `arkiv_query`, `arkiv_getEntityCount`, `arkiv_debugEntityExists` at the head | ✓ | identical (Arkiv's own query engine, cursors and errors) |
| … at a past block | `-32006` | `-32006` (same) — except `arkiv_debugEntityExists`, which answers from the archive |
| `arkiv_getBlockTiming` | ✓ | identical (checked at block 771 922) |
| `eth_getBlockByNumber("safe" / "finalized")` | ✓ | identical number and hash (checked at 0xbc72e / 0xbc70e) |
| revert messages (`eth_call`) | `execution reverted: <reason>` + `Error(string)` data | identical message and data |

## Block production

Nethermind builds Arkiv blocks as `arkiv-reth`'s payload builder does. First comes the protocol purge: the published key
signs it, `nonce` is the block number, and it names the entities expired by the block (oldest expiry first, then by
key), at most `MAX_PURGE_KEYS` within `PURGE_GAS_LIMIT`. Then the pool's transactions follow, each taken or refused by
Arkiv's executor (a refused one, e.g. a contract creation, is skipped). The root is that of the sealed but uncommitted
GolemDB branch. Checked three ways:

| Check | Result |
| --- | --- |
| Purge contents on Rabdi blocks 1–700 (native test) | the keys chosen at each block equal the keys the real purge carried |
| Purge transaction (C# test) | all 700 purge transactions rebuilt byte for byte: same hash |
| Rebuilding Rabdi blocks with a non-empty purge (native test) | same state root and gas used as the header |
| Devnet (`scripts/run-devnet.sh`, `tools/devnet_build.py`): Nethermind builds from its pool through `testing_buildBlockV1` and through `engine_forkchoiceUpdatedV3` + `engine_getPayloadV4`; each block goes to the official `arkiv-reth` v1.0.0 by `engine_newPayloadV4` | 20 blocks over two devnets, every one `VALID` on `arkiv-reth`: entity creates purged on expiry (blocks 4 and 7 for lifetimes 3 and 5), transfers, a refused contract creation skipped while the rest of the pool went in; entity count and balances equal on both nodes |

The devnet is Arkiv's `genesis/dev.json` plus Rabdi's genesis predeploys and without Osaka. As given, `dev.json` has no
EIP-4788/2935/7002/7251 contracts, so Nethermind refuses its blocks ("Contract is not deployed", as EIP-7002 requires
for a Prague block). Rabdi has them. A transaction Arkiv refuses stays in Nethermind's pool and is tried again at
each build. reth's pool validation would keep a contract creation out of the block too.

## State sync (`arkiv/1`)

Snap carries the account trie, which on Arkiv is neither the header's state root nor the state, so snap cannot sync an
Arkiv node, from Nethermind or from arkiv-reth. The plugin replaces the state phase of Nethermind's snap/fast sync:
- A new node fetches the GolemDB store copy a peer serves over the `arkiv/1` capability, at a block past its sync pivot.
- It installs the copy only if the copy's head commit is that block: tagged with its hash, with the root its header carries.
- It builds its local account trie from the store's accounts, taken from GolemDB's `#recordKeys` binding record, so
  every account is listed.
- It then executes the blocks from the next one on.

arkiv-reth has no equivalent: its nodes restore a published snapshot over HTTPS, unchecked.

| Run | Result |
| --- | --- |
| Devnet: node B joins from an archive node A with snap sync on; arkiv-reth validates every block | B installed A's store at block 146 (16 MiB), executed 147–179 and answered `VALID` from then on; node C then synced from B (a state-synced node serves too). At block 184, arkiv-reth, A and B agreed on 67 checks: block hash, balances and nonces of 28 accounts, entity count, a query page, 8 entities. B restarted cleanly ("state sync unnecessary") |
| Rabdi, a fresh node joining from the archive node of the sync report (block 780 017) | 4 215 MiB fetched in 18.2 s over loopback, the GolemDB root verified against block 780 017's header, 1 846 accounts in the local trie. Against the archive node at that block: 168 checks (balances and nonces of the 33 sampled addresses, entity count, a query page, 100 entities by key), 0 mismatches |
| Installing a real 4.1 GB Rabdi store (block 750 000) natively | accounts listed in 60 ms, store opened in 61 ms, 1 845 accounts read in 14 ms |

Notes:
- After the install, Nethermind's sync takes about a minute to start executing blocks. It looks for synced state only
  up to its best header, which reaches the synced block only after its forward header sync has run.
- On one host every devnet peer is 127.0.0.1, so `Network.FilterPeersByRecentIp=false` is needed for a peer to come
  back after a restart (`scripts/run-devnet.sh` sets it).
- Found while building this, in Nethermind itself: satellite protocols that start before a peer's `eth` handler were
  parked under the session's id alone, so of two parked on one session (here `arkiv` and `snap`) only the last reached
  the sync peer. Fixed in `ProtocolsManager` (`fix(network): attach every satellite protocol parked before eth`).

## Operation

| | arkiv-reth | Nethermind + plugin |
| --- | --- | --- |
| Joining | published snapshot (or `FROM_GENESIS=1`) | from genesis, every block executed; or state sync from a peer's store (`arkiv/1`), verified against the header |
| Reorg below the committed head | cannot rewind: the node stops ("the arkiv store cannot rewind") | restores a periodic GolemDB copy on the new branch and replays forward |
| Restart after a crash | store is the only record | store and Nethermind reconciled: blocks the store already holds are replayed from their recorded results |
| History | head only | archive: blocks, receipts, logs, accounts at every block, entities at every block |

## Not Rabdi: arkiv-op-geth / arkiv-op-reth

`arkiv-op-geth` and `arkiv-op-reth` are the earlier OP Stack product (an EVM precompile at `0x4400…0044` keeping
entities as accounts in the Merkle-Patricia trie). Their state root is an MPT root, so they cannot validate Rabdi's
headers — not even block 0: over the same genesis allocation the account trie's root makes block 0 hash differently
(the plugin logs both at genesis). The reference for Rabdi is `arkiv-reth`.
