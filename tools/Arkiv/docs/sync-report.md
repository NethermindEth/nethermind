# Archive sync and validation report — Rabdi

Everything here was run on one host (24 cores, 125 GiB RAM, NVMe, shared with other users) with
`scripts/run-rabdi.sh`: Nethermind `master` (dad2028c, .NET 11) with this plugin in archive configuration, and
Lighthouse v8.3.1 (built from its tag, redb store), both starting from **empty datadirs** and following the watchers
listed in `db-chain-networks/networks/testnet/rabdi/p2p.yaml`. No snapshot was imported; Lighthouse handed every
block to Nethermind over the Engine API and Nethermind executed it (Arkiv's executor over GolemDB, natively) and
validated it against its header — state root, receipts root, logs bloom, gas used, requests hash.

Reference for validation: the public RPC `rpc.rabdi.db-chain.testnet.arkiv.network` (arkiv-reth v1.0.0).

## Sync 1 — reached the tip; its validation found a bug

| | |
| --- | --- |
| Started | 2026-11-16 21:34:22 UTC, empty datadirs |
| Reached the tip (Lighthouse `sync_distance` 0, `is_optimistic` false) | 2026-11-17 00:31:28 UTC — **2 h 57 min**, head 744 794 |
| Followed the live head afterwards | 9 h (to block 760 765), latest block hash equal to the reference's at every check |
| Errors in the node log (all rotated files) | **0**; warnings are answers to RPC clients: 687 `eth_call` refusals of a purge-shaped call some process on the shared host sent once a minute (arkiv-reth refuses it the same way), 102 reverted `eth_call`s, 139 batch-size refusals, `-32006` for historical queries |
| Invalid blocks | none |
| Interruptions | `kill -9` at 17 332 (Nethermind's persisted head fell back to 16 995 while GolemDB was at 17 359: the 364 blocks were replayed from the recorded results in 0.6 s and execution resumed at 17 360); two clean restarts (persisted heads 64 156 and 85 050) |
| Datadir | Nethermind 20.0 GB (GolemDB 4.1 GB, two GolemDB copies 8.0 GB, account trie 3.6 GB, block results and entity archive 2.8 GB, blocks 1.1 GB), Lighthouse 3.5 GB |
| Peak RSS | 28.5 GB (Server GC retention under sustained import; 6–10 GB once following the head) |

Differential validation at the tip (`tools/differential.py`, 400 random heights over the whole chain + 414 vectors):
headers, transactions, all 634 receipts and 100 log windows matched. **Historical balances and nonces did not**: every
account read at a past block returned its genesis value. Cause: the native result listed the accounts a block wrote
from revm's bundle without merging its transitions first, so the bundle was always empty and nothing was mirrored into
the account trie (consensus was unaffected — the header root is GolemDB's and matched on every block). Fixed, with a
test that fails without the fix; a second, also found here, made `arkiv_getEntity` refuse a numeric
block argument. Sync 1's account history being wrong, the archive sync was repeated from genesis with the fixed build.

## Sync 2 — the acceptance run

Same setup, fixed build, empty datadirs for both Nethermind and Lighthouse.

| | |
| --- | --- |
| Started | 2026-11-17 09:44:27 UTC, empty datadirs |
| Reached the tip (Lighthouse `sync_distance` 0, `is_syncing` false, `is_optimistic` false) | 2026-11-17 12:40:05 UTC — **2 h 55 min 38 s**, head 766 650 |
| Blocks executed and validated from genesis | 766 650 to catch-up; every block since followed at the head (769 763 at the last check) |
| Following the head | latest block number and hash equal to the reference's at every check (e.g. `0xbbd7a` … `0xbbd84`, `0xbbe8b`) |
| Errors in the node log (all rotated files) | **1**: the disk guard's own shutdown (below) |
| Invalid blocks | **0** |
| Interruptions | at 213 000 the shared volume another job was filling dropped below 1 % free and Nethermind's disk guard shut the node down cleanly; both datadirs were moved to another volume and the sync **resumed** at 213 000 (1.5 min outage). Clean restart at the tip (769 667): stopped in 5 s, back at the reference's head 11 s after starting |
| Datadir at the tip | Nethermind 21 GB — GolemDB 4.1 GB, two GolemDB copies 8.1 GB, account trie (archive) 4.3 GB, block results + entity archive 2.5 GB, blocks 1.1 GB, receipts 0.2 GB; Lighthouse 3.8 GB |
| Memory | peak RSS 29.9 GB during import (.NET Server GC retention under sustained load); 6.5–7.4 GB while following the head after the import, 1.0 GB right after the restart at the tip, 4.5 GB an hour later |
| Block processing at the head | 4.5 ms p50, 15 ms p90 (last 40 blocks) |

### Validation against the reference

`tools/differential.py --samples 500 --seed 31337 --vectors samples.jsonl` at the tip (node and reference both at 769 690):

| Check | Count | Mismatches |
| --- | ---: | ---: |
| `eth_getBlockByNumber` (hashes and full transactions) at random heights over the whole chain | 1 000 | 0 |
| `eth_getTransactionReceipt` for every transaction of those blocks | 784 | 0 |
| `eth_getLogs` windows (against the logs of the reference's receipts; its own `eth_getLogs` fails) | 125 | 0 |
| historical `eth_getBalance` — ground truth recorded at the reference's head (`tools/reference_sampler.sh`, blocks 738 985 – 769 6xx) | 9 381 | 0 |
| historical `eth_getTransactionCount` (same vectors) | 9 381 | 0 |
| historical `arkiv_getEntity` from the node's entity archive (same vectors) | 3 988 | 0 |
| block headers of the vectors | 2 198 | 0 |
| live, both at block 769 763: balances, nonces, `entityNonce` via `eth_call`, storage, code, entities, entity count, query pages | 37 | 0 |
| `eth_getProof` against `eth_getBalance` | 30 | 0 |
| **Total** | **27 474** | **0** |

Historical `arkiv_query` / `arkiv_getEntityCount` (2 198 vectors each): the reference answered them when the block was its
head; at a past block both clients refuse with `-32006` — GolemDB v1.0.0 reads at its head only. A capability
neither has, reported apart.

### Requirement by requirement

| Requirement | Result |
| --- | --- |
| No imported execution-state snapshot | none: empty datadirs, the `run-node.sh` snapshot step not used |
| No skipped historical execution | every block executed by Arkiv's executor and validated against its header |
| No pruned historical state | `Pruning.Mode=None`; account state at any block, entity state after any block |
| Reach the current head and keep following | yes (above) |
| State roots and receipts against the reference | every header's state root and receipts root checked on execution; 1 000 blocks and 784 receipts compared field by field |
| Historical balances, storage, logs, receipts, calls, Arkiv queries | as tabled; historical `eth_call` runs at the head on both clients |
| Consistency across restarts | crash (sync 1), two clean restarts (sync 1), stop + move + resume and a restart at the tip (sync 2) |
| Recovery from interrupted sync | `kill -9` mid-sync replayed 364 committed blocks from their recorded results and resumed |
| Reorganisations | none occurred on Rabdi during either sync. Covered by tests on real blocks: a sibling of block 101 restores the GolemDB copy at 100 and replays, then the original branch is restored the same way; without a copy the node refuses rather than diverges |

## State sync on Rabdi (`arkiv/1`)

A fresh node (Nethermind + plugin, `Sync.SnapSync=true`, empty datadir) joined from the archive node above, its only
peer, at block 780 017. The public watchers' beacon nodes were down for maintenance at the time, so a script played the
consensus client: forkchoice at 780 016 (the sync pivot), then block 780 017's payload.

| | |
| --- | --- |
| Headers 0 – 780 016 from the archive node | under 2 s |
| Store copy served by the archive node at its head, 780 017 | made in about 2 s, 7 files, 4 215 MiB |
| Fetched over `arkiv/1` (loopback, 1 MiB chunks, 2 in flight) | 18.2 s, up to 356 MiB/s |
| Checked | head commit tagged with block 780 017's hash, root equal to its header's `stateRoot` |
| Local account trie | 1 846 accounts, from the store's `#recordKeys` |
| Against the archive node at 780 017 | 168 checks (balances, nonces, entity count, query page, 100 entities), 0 mismatches |
