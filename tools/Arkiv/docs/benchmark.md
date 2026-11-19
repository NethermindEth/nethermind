# Benchmark: Nethermind + Arkiv plugin vs arkiv-reth v1.0.0

## What is compared

- **Reference**: `arkiv-reth` built from [Arkiv-Network/arkiv](https://github.com/Arkiv-Network/arkiv) tag `v1.0.0`
  (827a83b; reth fork a91d851, golemdb aa20dbe) — the source of `ghcr.io/arkiv-network/arkiv-reth:v1.0.0`, the image
  Rabdi runs (`rabdi/images.env`). Release build, `--locked`, default settings (GolemDB head-only history).
- **Nethermind**: `master` (dad2028c, .NET 11) with this plugin, `configs/rabdi.json` (archive: `Pruning.Mode=None`,
  entity history on).

`arkiv-op-reth` / `arkiv-op-geth` are not compared: they are the earlier OP Stack design (entities in the
Merkle-Patricia trie, an MPT state root) and cannot validate a single Rabdi block — over Rabdi's genesis allocation
the account trie's root is `0x385d7382…bd58`, which would make block 0 a different block than Rabdi's `0xa65a2b9e…cbce`.

## Method

`tools/benchmark.sh` starts one client from an empty datadir with networking off and `tools/engine_feed.py` sends it
the same `engine_newPayloadV4` stream, one `engine_forkchoiceUpdatedV3` (head = safe = finalized) after each payload.
The payloads are Rabdi's own blocks (from `eth_getBlockByNumber`, converted by `src/native/arkiv-ffi/examples/payloads.rs`,
each re-hashed to its block hash). A run fails at the first payload not answered `VALID`, so every block was fully
executed and validated by both clients. Reported: wall time, newPayload latency percentiles (round trip from the
feeder), peak RSS (`VmHWM`, sampled every 5 s), datadir size at the end.

Host: 24 cores, 125 GiB RAM, NVMe (md RAID). The live archive sync ran on the same host throughout, so absolute
numbers carry that load equally for both clients.

## Preliminary: blocks 1 – 103 000

Run one after the other, but unpinned and while the live archive sync was importing ~70 blocks/s on the same
cores; superseded by the pinned series below.

103 000 blocks, 105 903 transactions (2 903 user transactions, the rest protocol purges).

| | arkiv-reth v1.0.0 | Nethermind + plugin |
| --- | --- | --- |
| Wall time | 764.9 s | **629.5 s** |
| Throughput | 134.7 blocks/s | **163.6 blocks/s** (+21 %) |
| newPayload p50 | 4.02 ms | **3.00 ms** |
| newPayload p90 | 12.04 ms | **9.92 ms** |
| newPayload p99 | 38.55 ms | **32.13 ms** |
| newPayload max | **218 ms** | 386 ms |
| Peak RSS | **593 MB** | 9 422 MB |
| Datadir at end | 4 441 MB | **1 102 MB** (archive) |

Peak RSS: Nethermind runs .NET **Server GC** (`ServerGarbageCollection` in `Nethermind.Runner.csproj`: one heap per
core, 24 here), which on a host with plenty of free memory lets freed objects pile up while blocks keep arriving.
Measured on two extra 103k runs with metrics: RSS climbed to ~8 GB under the feed and fell to **0.64–1.4 GB within
seconds of the feed ending** — the same with archive (`Pruning.Mode=None`) and default (`Hybrid`) pruning. The
native shim alone (the replay example, no .NET) stays at 16–23 MB anonymous memory over 203 000 blocks. So the
figure is GC retention under load, not live data, not archive mode and not the plugin.

Datadir breakdown at the end — arkiv-reth: reth's MDBX `db/` 4.1 GB, GolemDB 339 MB (257 MB MDBX + 83 MB
immutable segments with headers, transactions and receipts). Nethermind: account-trie `state` 386 MB, `arkiv`
515 MB (GolemDB plus the per-block results and the entity archive), blocks 55 MB, receipts 33 MB, headers 24 MB.

## Full chain: genesis – 760 765 (the acceptance series)

A series script over the corpus recorded from the validated node: each client alone from an empty datadir, pinned
to cores 8–23 (feeder on 6–7), Lighthouse's forkchoice lags (safe −31, finalized −63), the live node stopped. The only
other load on the host was one unrelated process pinned to core 1 (`bench/hostload.log`). Every payload `VALID`.
Nethermind master dad2028c with this plugin; arkiv-reth v1.0.0.

| | arkiv-reth v1.0.0 | Nethermind + plugin (archive) |
| --- | ---: | ---: |
| Blocks / transactions | 760 765 / 1 202 860 | 760 765 / 1 202 860 |
| Wall time | 3 959.8 s (1 h 06 min) | **2 839.6 s (47 min)** |
| Throughput | 192.1 blocks/s | **267.9 blocks/s** (+39 %) |
| Average per block (wall / blocks) | 5.21 ms | **3.73 ms** |
| newPayload p50 | 3.84 ms | **2.03 ms** |
| newPayload p90 | 6.16 ms | **5.39 ms** |
| newPayload p99 | **15.37 ms** | 17.65 ms |
| newPayload max | **159 ms** | 2 449 ms (a GolemDB checkpoint copy) |
| Client CPU time per block | 6.06 ms | 6.04 ms |
| Peak RSS | **5.2 GB** | 39.2 GB (Server GC retention under the feed, see above) |
| Datadir at end | **10.4 GB** | 21.0 GB |

Nethermind's datadir: GolemDB 4.2 GB, **two full GolemDB copies for reorg recovery 8.2 GB**, account-trie archive
4.4 GB, block results and entity history 2.5 GB, blocks/headers/receipts 1.5 GB. arkiv-reth's: GolemDB 5.9 GB (with
1.3 GB of headers, transactions and receipts), reth's MDBX 4.1 GB (the same size at block 103 000: preallocated).
The copies are the gap: one, taken at a finalized block, would do (a reorg cannot pass finality), and block results
older than it are not needed — the next change.

## First 200 000 blocks (same series)

| | arkiv-reth v1.0.0 | Nethermind + plugin |
| --- | ---: | ---: |
| Wall time | 859.5 s | **473.3 s** |
| Throughput | 232.7 blocks/s | **422.6 blocks/s** |
| newPayload p50 / p90 / p99 | 3.41 / 3.96 / 7.63 ms | **1.66 / 2.27 / 7.59 ms** |
| Client CPU time per block | 4.72 ms | 4.52 ms |
| Peak RSS | **1.0 GB** | 12.3 GB |
| Datadir at end | 4.9 GB | **2.6 GB** |
