# Persisted-snapshot sorted-table format

A persisted snapshot's metadata blob is a single, deliberately-unoptimized, **one-level sorted
table** (`SortedTable`). It replaces the previous columnar HSST format. Trie-node RLP still lives in
separate blob arenas; the table stores only small inline values (account RLP, slot RLP, 6-byte
`NodeRef`s, self-destruct flags, metadata).

## Layout (within the table's `Bound`, offsets relative to the bound start)

```
records:  [keysize u16][key][valuesize u16][value]  × N      (records in arbitrary insertion order)
offsets:  [recordOffset u32]                         × N      (one per record, in ascending key order)
footer:   [count i64][version u8]                              (fixed 9 bytes, read first)
```

- The **offset region** is the only sorted structure: `offsets[i]` is the byte offset of the i-th
  record *in ascending key order*. Lookups read the footer for `N`, then binary search the offset
  region — each probe reads `offsets[mid]`, seeks the record, and compares its inline key
  (`SortedTableReader`). O(log N) reader accesses, no caching, no per-table bloom.
- The **builder** (`SortedTableBuilder`) streams records to the writer in any order, buffers every
  key off-heap, and sorts the offsets once at `Build`. Buffering all keys is the intended cost.
- `version` byte rejects a blob written by a different format; the catalog version
  (`SnapshotCatalog`) gates the whole tier across incompatible changes.

## Keys (`PersistedSnapshotKey`)

The table is plain ascending byte-sorted — no custom comparator. To reproduce the HSST reverse-tag
emission order (DenseByteIndex containers wrote tags descending), the **column and subcolumn tag
bytes are stored as `255 − tag`**; entity bytes are natural. Ascending order then is:

| Entity | Key bytes (tags as 255−v) | Value |
|---|---|---|
| Storage node | `FA` + addrHash(20) + `{FF top, FE compact, FD fallback}` + path | `NodeRef` (6) |
| State node | `{FD top, FC compact, FB fallback}` + path | `NodeRef` (6) |
| Slot | `FE` + addr(20) + `FD` + slot(32 BE) | RLP-wrapped value / empty (deleted) |
| Self-destruct | `FE` + addr(20) + `FE` | `[00]` destructed / `[01]` new |
| Account | `FE` + addr(20) + `FF` | slim account RLP / `[00]` deleted |
| Metadata | `FF` + name(10, NUL-padded) | metadata value |

Within an address: slots → self-destruct → account. Within an addressHash: fallback → compact →
top. Across columns: storage → state → per-address → metadata. The path encodings (4/8/33-byte) and
the per-bucket ordering are unchanged from the HSST builder/compacter so a future proper-HSST
serializer can reuse them.

## Compaction (`PersistedSnapshotMerger`)

Each input snapshot is one sorted run. The merge walks them in ascending key order (O(N) find-min),
newest-source-wins per key. Slots are buffered per address and flushed once that address's
self-destruct barrier is known — slots that contributed only from sources older than the newest
destruct are dropped (self-destruct truncation). Metadata is merged separately: `from_*` from the
oldest source, `to_*`/`version` from the newest, the union of all `ref_ids`, and a `noderefs`
presence marker.
