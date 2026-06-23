# Persisted-snapshot sorted-table format

A persisted snapshot's metadata blob is a single **two-level sorted table** (`SortedTable`), laid out
like a LevelDB SSTable: a run of 4 KiB-aligned data blocks plus one index block, both using the same
self-describing block format. It replaces the previous columnar format. Trie-node RLP still lives
in separate blob arenas; the table stores only small inline values (account RLP, slot RLP, 6-byte
`NodeRef`s, self-destruct flags, metadata).

## Layout (within the table's `Bound`, offsets relative to the bound start)

```
data block × M  ; blocks 0..M-2 zero-padded to BlockSize (4096); data block i at i·BlockSize
index block     ; right after the last (unpadded) data block, at the footer's indexOffset; NOT block-aligned;
                ;   key = separator, value = u32 blockNumber LE
footer          ; [count i64][numDataBlocks i64][indexOffset i64][restartInterval u8][version u8]  (fixed 26 bytes, read first)

Block (data and index alike):
  [offsetWidth u8]                    ; W = 2 or 4 bytes
  [recordsEnd  : W]                   ; block-relative byte offset where records end (content size)
  [numRestarts : W]
  [restartOffset : W × numRestarts]   ; block-relative; restartOffset[0] = 1 + 2W + W·numRestarts
  [records...]                        ; [cp u8][suffixLen u8][keySuffix][vs u8][value]
```

- Both levels reuse one `Block` (`Block.cs`). Within a block, keys are **front-coded**: `cp` is the
  number of leading bytes shared with the previous record's key and `keySuffix` is the remaining
  `suffixLen` bytes, so the full key = previous key's first `cp` bytes + `keySuffix`. Front-coding
  **resets** (`cp = 0`, full key) every `restartInterval` (default **8**) records and at every block
  start — these reset points are the **restarts**, and each block prefixes a table of their byte
  offsets. The per-block **`offsetWidth`** (`W`) is the narrowest of 2 or 4 bytes that addresses the
  finished block: a ≤ 64 KiB data block uses `W = 2`, the multi-MB index uses `W = 4`. `recordsEnd`
  lets a block be located by its **start alone** — crucial because data blocks are zero-padded; the
  scan/enumeration stops at `recordsEnd` and never reads pad bytes. `cp`, `suffixLen`, and the value
  size `vs` are each one byte: keys are ≤ 55 bytes, every inline value is < 255. The one variable-length
  datum, the referenced blob-arena id list, is stored as separate records (see below), so no value
  overflows.
- Records are **streamed and packed** into data blocks in ascending key order; a data block closes once
  the next record would push its content past `BlockSize` (4096). Blocks 0..M-2 are **zero-padded to
  4096** so block `i` sits at `i·BlockSize` and is addressed by **block number** — a `u32` block number
  times 4096 reaches a 16 TiB data region. The **last** data block is left unpadded, with the index
  block immediately after it.
- The **index block** maps, per data block, the shortest **separator** key in
  `[lastKey(block), firstKey(next block))` (the last block's separator is its own last key) to that
  block's number. It is located directly by the footer's `indexOffset` (a table-relative byte offset),
  so it needs no block-number address and no padding; the i64 footer fields span the full range.
- A lookup (`SortedTableReader`) reads the footer, then does two `BlockReader.SeekCeiling` calls
  (LevelDB `Block::Iter::Seek`): (1) ceiling over the **index block** — the first separator ≥ the
  target yields the data block number (a target past the last separator misses); (2) ceiling over that
  **data block** — the first key ≥ the target; a hit requires that key to **equal** the target. Each
  ceiling binary-searches the restarts (rightmost restart whose first key ≤ target, clamped to restart
  0 when the target precedes the block) then scans forward to `recordsEnd`, reconstructing front-coded
  keys. O(log M) + O(log restarts) random reads + a short in-page scan; no caching, no per-table bloom.
- The **builder** (`SortedTableBuilder`) requires records in **strictly ascending** key order and
  streams them straight into a data `BlockBuilder` (closing + padding at 4096) as they arrive — no
  record buffer, so the table size is bounded by the 16 TiB data region rather than by memory. The index
  `BlockBuilder` (separator → block number) accrues one entry per flushed data block; only the current
  data block and the index are held in memory. Producers (`PersistedSnapshotBuilder`,
  `PersistedSnapshotMerger`) therefore emit in ascending key order (see Keys below).
- `version` rejects a blob written by a different format; the catalog version (`SnapshotCatalog`)
  gates the whole tier across incompatible changes.

## Keys (`PersistedSnapshotKey`)

The table is plain ascending byte-sorted — no custom comparator. To reproduce the columnar reverse-tag
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
per-bucket ordering are unchanged from the columnar builder/compacter so a future proper columnar serializer
can reuse them.

## Compaction (`PersistedSnapshotMerger`)

Each input snapshot is one sorted run. The merge walks them in ascending key order (O(N) find-min),
newest-source-wins per key. Ref-id records dedup through this same merge, yielding the union of
referenced ids for free. Slots are buffered per address and flushed once that address's
self-destruct barrier is known — slots that contributed only from sources older than the newest
destruct are dropped (self-destruct truncation). The remaining metadata (`from_*` from the oldest
source, `to_*`/`version` from the newest, a `noderefs` presence marker) is written separately.
