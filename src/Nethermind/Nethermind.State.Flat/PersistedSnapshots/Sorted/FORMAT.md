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
                ;   key = separator, value = data-block table-relative byte offset (u48), changed low bytes only
footer          ; [indexOffset i64][version u8]  (fixed 9 bytes, read first)

Block (data and index alike):
  [formatFlag u8]                     ; Block => W = 2, Index => W = 4 (offset width in bytes)
  [recordsEnd  : W]                   ; block-relative byte offset where records end (content size)
  [numRestarts : W]
  [restartOffset : W × numRestarts]   ; block-relative; restartOffset[0] = 1 + 2W + W·numRestarts
  data record                         ; [cp u8][suffixLen u8][valueLen u8][keySuffix][value]
  index record                        ; [cp u8][suffixLen u8][valChangedLen u8][keySuffix][valChanged]
```

- Both levels reuse one `Block` (`Block.cs`). Within a block, keys are **front-coded**: `cp` is the
  number of leading bytes shared with the previous record's key and `keySuffix` is the remaining
  `suffixLen` bytes, so the full key = previous key's first `cp` bytes + `keySuffix`. A record with
  `cp = 0` (full key) is a **restart**: the builder forces one at least every `restartInterval`
  (default **8**) records — a build-time knob, **not stored on disk** — to bound scan length, and one
  also arises wherever adjacent keys share no leading byte. Each block prefixes a table of the byte
  offsets of all its restarts. The header **`formatFlag`** records the block's role and thereby its offset width `W`: a
  data **`Block`** (capped well under 64 KiB) uses `W = 2`, the multi-MB **`Index`** uses `W = 4`. `recordsEnd`
  lets a block be located by its **start alone** — crucial because data blocks are zero-padded; the
  scan/enumeration stops at `recordsEnd` and never reads pad bytes. Each record opens with a fixed,
  single-byte-field prefix carrying every length, so a reader blits the prefix then slices the key (and
  inline value) after it. In a **data record** `valueLen` is the inline value's byte count (`cp`,
  `suffixLen`, `valueLen` each one byte: keys are ≤ 55 bytes, every inline value is < 255 — the one
  variable-length datum, the referenced blob-arena id list, is stored as separate records, so no value
  overflows). In an **index record** the value is stored as `valChangedLen` (≤ 6) low little-endian bytes —
  only the bytes that differ from the previous record's value (see below).
- Records are **streamed and packed** into data blocks in ascending key order; a data block closes once
  the next record would push its content across a 4 KiB page (`WouldCrossPage`). Blocks 0..M-2 are
  **zero-padded to 4096** so block `i` sits at `i·BlockSize`; the index records each block's
  table-relative **byte offset** (a `u48` reaches a 256 TiB data region). The **last** data block is left
  unpadded, with the index block immediately after it. Byte-offset addressing no longer *requires* the
  4 KiB alignment, but it is kept so reads stay page-aligned and block `i` stays at `i·BlockSize`.
- The **index block** maps, per data block, the shortest **separator** key in
  `[lastKey(block), firstKey(next block))` (the last block's separator is its own last key) to that
  block's table-relative **byte offset**, stored **little-endian** as only the low bytes that changed from
  the previous record's value (`valChangedLen` = their count): the reader keeps the unchanged high bytes
  and overwrites the low ones in place, fully restating the value (against 0) at every restart (`cp = 0`).
  Offsets ascend, so the high bytes rarely change and `valChanged` stays short; the little-endian layout
  lets the reader copy `valChanged` straight onto the low end of a running value. It is located directly by
  the footer's `indexOffset`, so it needs no block-number address and no padding; that i64 offset spans the
  full range.
- A lookup (`SortedTableReader`) reads the footer, then does two ceiling searches
  (LevelDB `Block::Iter::Seek`): (1) `IndexBlockReader.SeekCeiling` over the **index block** — the first
  separator ≥ the target yields the data block's byte offset, reconstructing the changed-byte value (a
  target past the last separator misses); (2) `DataBlockReader.SeekCeiling` over that **data block** — the
  first key ≥ the target; a hit requires that key to **equal** the target. Each ceiling binary-searches the
  restarts (rightmost restart whose first key ≤ target, clamped to restart 0 when the target precedes the
  block) then scans forward to `recordsEnd`, reconstructing front-coded keys. O(log M) + O(log restarts)
  random reads + a short in-page scan; no caching, no per-table bloom.
- A full scan (`SortedTableEnumerator`) walks the **index block** in order, reconstructing each value from
  its changed low bytes to get the next data block's byte offset, then emits that block's records — so
  iteration relies on the index, not on the 4 KiB alignment (which is kept only for page-aligned reads).
- The **builder** (`SortedTableBuilder`) requires records in **strictly ascending** key order and
  streams them straight into a data `BlockBuilder` (closing + padding at 4096) as they arrive — no
  record buffer, so the table size is bounded by the 256 TiB data region rather than by memory. The index
  `BlockBuilder` (separator → byte offset) accrues one entry per flushed data block; only the current
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
