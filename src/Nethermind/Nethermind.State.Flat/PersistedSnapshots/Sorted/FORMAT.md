# Persisted-snapshot sorted-table format

A persisted snapshot's metadata blob is a single **two-level sorted table** (`SortedTable`), laid out
like a LevelDB SSTable: size-bounded data blocks plus a separator-key index at the tail. It replaces
the previous columnar HSST format. Trie-node RLP still lives in separate blob arenas; the table stores
only small inline values (account RLP, slot RLP, 6-byte `NodeRef`s, self-destruct flags, metadata).

## Layout (within the table's `Bound`, offsets relative to the bound start)

```
data block × M: [numRestarts u16][restartOffset u16 × numRestarts][records...]
                records: [cp u8][suffixLen u8][keySuffix][vs u8][value]
separators:     [sepLen u8][sep bytes]   × M
sep offsets:    [sepEntryOffset u32]      × M       (first-level binary search operates on this)
block offsets:  [blockDataOffset u32]     × (M + 1) (last entry = separators-region start = data end)
footer:         [count i64][numBlocks u32][restartInterval u8][version u8]   (fixed 14 bytes, read first)
```

- Records are physically **sorted and packed back-to-back** into **`BlockSizeTarget` (= 4096) byte**
  data blocks (a block closes once the next record would push it past the target). Within a block,
  keys are **front-coded**: `cp` is the number of leading bytes shared with the previous record's key
  and `keySuffix` is the remaining `suffixLen` bytes, so the full key = previous key's first `cp`
  bytes + `keySuffix`. Front-coding **resets** (`cp = 0`, full key) every `RestartInterval` (= 16)
  records and at every block start — these reset points are the **restarts**, and each block prefixes
  a table of their byte offsets (relative to the block start, a `u16` since a block stays well under
  64 KiB; `restartOffset[0] = 2 + 2·numRestarts`). `cp`, `suffixLen`, and the value size `vs` are
  each one byte: keys are ≤ 55 bytes, and every inline value is < 255 (the builder's checked cast
  enforces it). The one variable-length datum, the referenced blob-arena id list, is stored as
  separate records instead (see below), so no value overflows.
- The **tail index** stores, per block, the shortest **separator** key in
  `[lastKey(block), firstKey(next block))` (the last block's separator is its own last key), the
  separators' offsets, and the blocks' data offsets. The two fixed-width offset arrays sit **last** so
  the footer locates them from `numBlocks` alone (the separators region is variable-length).
- A lookup (`SortedTableReader`) reads the footer, then: (1) **lower-bound binary search** of the
  separators — the first block whose separator ≥ the target (a separator may be a synthetic key in no
  block, so stage 3 re-validates; a target past the last separator misses); (2) **binary search** of
  that block's restart table for the rightmost restart whose first key ≤ the target (restart-start
  keys are full, `cp = 0`; a target before the block's first key misses); (3) **sequentially scan**
  that restart run, reconstructing each key into a running buffer (keep `[0..cp)`, append the suffix),
  stopping at the match, at a greater key, or at the run's end. O(log M) + O(log restarts) random
  reads + a short in-page scan; no caching, no per-table bloom.
- The **builder** (`SortedTableBuilder`) buffers records off-heap (full keys, any order), sorts them
  by key at `Build`, then streams the data blocks (only the current block and the small tail index are
  held in memory), followed by the separators, the offset arrays, and the footer. The block index
  keeps the first-level search off the data pages; front-coding shrinks the dominant long,
  prefix-sharing keys (slots, storage/state nodes, accounts).
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
