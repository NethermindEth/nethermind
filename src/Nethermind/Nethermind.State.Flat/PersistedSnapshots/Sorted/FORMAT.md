# Persisted-snapshot sorted-table format

A persisted snapshot's metadata blob is a single, deliberately-unoptimized, **one-level sorted
table** (`SortedTable`). It replaces the previous columnar HSST format. Trie-node RLP still lives in
separate blob arenas; the table stores only small inline values (account RLP, slot RLP, 6-byte
`NodeRef`s, self-destruct flags, metadata).

## Layout (within the table's `Bound`, offsets relative to the bound start)

```
records:  [cp u8][suffixLen u8][keySuffix][vs u8][value]  × N   (sorted by key, contiguous, front-coded)
offsets:  [recordOffset u32]           × ceil(N / 8)            (first record of each 8-record block)
footer:   [count i64][blockSize u8][version u8]                 (fixed 10 bytes, read first)
```

- Records are physically **sorted and packed back-to-back**, with keys **front-coded**: `cp` is the
  number of leading bytes shared with the previous record's key and `keySuffix` is the remaining
  `suffixLen` bytes, so the full key = previous key's first `cp` bytes + `keySuffix`. The first
  record of every block has `cp = 0` (full key) so the block decodes standalone. `cp`, `suffixLen`,
  and the value size `vs` are each one byte: keys are ≤ 55 bytes, and every inline value is < 255
  (the builder's checked cast enforces it). The one variable-length datum, the referenced blob-arena
  id list, is stored as separate records instead (see below), so no value overflows.
- The **sparse offset region** stores the byte offset of the first record of every `blockSize`
  (= 8) record block, in ascending key order. A lookup (`SortedTableReader`) reads the footer for
  `count`/`blockSize`, binary searches the sparse offsets for the block whose first key ≤ the
  target (block-start keys are full, `cp = 0`), then **sequentially scans that block's ≤ 8
  contiguous records**, reconstructing each key into a running buffer (keep `[0..cp)`, append the
  suffix). Almost always within one 4 KiB page; O(log(N/8)) random reads + a short in-page scan; no
  caching, no per-table bloom.
- The **builder** (`SortedTableBuilder`) buffers records off-heap (full keys, any order), sorts them
  by key at `Build`, then writes the sorted, front-coded records, the sparse offset region, and the
  footer. The sparse index cuts the offset region and per-record build bookkeeping ~8×; front-coding
  shrinks the dominant long, prefix-sharing keys (slots, storage/state nodes, accounts).
- `version` rejects a blob written by a different format; the catalog version (`SnapshotCatalog`)
  gates the whole tier across incompatible changes.

## Keys (`PersistedSnapshotKey`)

The table is plain ascending byte-sorted — no custom comparator. To reproduce the HSST reverse-tag
emission order (DenseByteIndex containers wrote tags descending), the **column and subcolumn tag
bytes are stored as `255 − tag`**; entity bytes are natural. Ascending order then is:

| Entity | Key bytes (tags as 255−v) | Value |
|---|---|---|
| Ref-id | `00` + blobArenaId(2 BE) | `[01]` presence |
| Storage node | `FA` + addrHash(20) + `{FF top, FE compact, FD fallback}` + path | `NodeRef` (6) |
| State node | `{FD top, FC compact, FB fallback}` + path | `NodeRef` (6) |
| Slot | `FE` + addr(20) + `FD` + slot(32 BE) | RLP-wrapped value / empty (deleted) |
| Self-destruct | `FE` + addr(20) + `FE` | `[00]` destructed / `[01]` new |
| Account | `FE` + addr(20) + `FF` | slim account RLP / `[00]` deleted |
| Metadata | `FF` + name(10, NUL-padded) | metadata value |

Each referenced blob-arena id is its own record under column `00`, which sorts before every real
column — so the ref-ids are the first records and iterate cheaply from the table start
(`PersistedSnapshot`'s ref-id enumerator stops at the first non-`00` record). Within an address:
slots → self-destruct → account. Within an addressHash: fallback → compact → top. Across columns:
ref-ids → storage → state → per-address → metadata. The path encodings (4/8/33-byte) and the
per-bucket ordering are unchanged from the HSST builder/compacter so a future proper-HSST serializer
can reuse them.

## Compaction (`PersistedSnapshotMerger`)

Each input snapshot is one sorted run. The merge walks them in ascending key order (O(N) find-min),
newest-source-wins per key. Ref-id records dedup through this same merge, yielding the union of
referenced ids for free. Slots are buffered per address and flushed once that address's
self-destruct barrier is known — slots that contributed only from sources older than the newest
destruct are dropped (self-destruct truncation). The remaining metadata (`from_*` from the oldest
source, `to_*`/`version` from the newest, a `noderefs` presence marker) is written separately.
