# HSST — Hierarchical Static Sorted Table

A compact, immutable binary format for sorted key/value tables.

## Document guideline

- This document specifies the **byte format** only. It must not reference any
  implementation type, method, file path, or other code artefact. If you need
  to describe how a particular reader/writer/iterator works, that belongs in
  source-code comments, not here. The format must be readable in isolation.

## Aim

- **Indexable blob.** An HSST is a self-contained byte sequence that can be
  point-queried (by key) without loading the whole blob — readers walk an
  embedded B-tree index from the tail to descend to the entry they want.
- **Hierarchical.** A value associated with a key may itself be an HSST blob
  ("nested HSST"). This is the expected shape, not a corner case: a column
  whose values are inner tables uses one outer HSST plus N inner HSSTs. Two
  consequences fall out of allowing values to be large:
  1. **Metadata sits *after* its value.** With variable-length values that
     can be many KiB or MiB long, putting a length prefix in front would
     force readers to consume the length even when they only want the
     adjacent metadata. Trailing metadata lets the reader pivot directly off
     the metadata cursor and back-decode the value's start.
  2. **Inner-HSST indexes end up next to the outer metadata.** The B-tree
     index of an HSST lives at the *end* of the blob. So when a value is
     itself an HSST, its index sits at the tail of the value bytes — i.e.,
     immediately before the outer entry's metadata. A reader that descends
     into a nested HSST and then ascends back to the outer level needs only
     the bytes near the cursor; the layout makes that locality natural.
- **Easy to iterate, hence easy to merge.** Entries within a node are sorted
  by key, and the B-tree imposes the same total order across nodes. Readers
  can walk an HSST left-to-right in sorted key order without buffering, and
  N-way merges of independent HSSTs need only one cursor per source.

## Top-level layout

| Variant | Bytes |
|---|---|
| **BTree** | `[Data Region][Index Region][RootSize: u16 LE][KeyLength: u8][IndexType: u8 = 0x01]` |
| **PackedArray** | `[Data][Summary L0]…[Summary L(D-1)][HashTable: 4·TableSize bytes (omitted when 0)][Metadata][MetadataLength: u8][IndexType: u8 = 0x02]` |
| **DenseByteIndex** | `[Value_0]…[Value_{N-1}][Ends: N·u32 LE][Count: u8 = N − 1][IndexType: u8 = 0x04]` |

The trailing **index type byte** is the last byte of the HSST and selects
the variant by enumerated value (not a bitfield):

| Value | Name | Meaning |
|---|---|---|
| `0x01` | `BTree` | Separate data region; leaves hold metaStart pointers. Fixed key length recorded once in the trailer rather than per entry. |
| `0x02` | `PackedArray` | Fixed-size key/value array with a recursive "summary" index and an optional hash table. |
| `0x03` | _reserved_ | Previously `ByteTagMap`; do not reuse without bumping the wire format. |
| `0x04` | `DenseByteIndex` | Single-byte-keyed map indexed directly by the tag byte; gap-filled with zero-length values. |

Other values are reserved for future index strategies. The root B-tree
node lives just before the BTree trailer (`[RootSize u16 LE][KeyLength u8][IndexType u8]`)
and is located by computing `root_start = HSST_end - 4 - RootSize`.

### BTree variant

The BTree HSST stores a fixed key length per blob: every entry in the
table has a key of exactly `KeyLength` bytes (0–255), recorded once in the
trailer's `KeyLength: u8` field. The data region is a packed sequence of
variable-length, **self-describing** entries laid out value-first so that
decoding is forward-readable from a known `MetadataStart` cursor:

```
[Value: V bytes][ValueLength: LEB128][FullKey: KeyLength bytes]
                ^
                MetadataStart  (= the index pointer's target byte)
```

`MetadataStart` is the byte offset (within the HSST buffer, measured from
byte 0 — the first byte of the data region) of the `ValueLength` LEB128.
The leaf B-tree node stores this offset for every entry; readers seek into
the leaf, take the metaStart pointer, then:

1. Decode `ValueLength` (LEB128) — the value bytes live at
   `[MetadataStart - ValueLength, MetadataStart)`.
2. The full key sits at
   `[MetadataStart + lebBytes, MetadataStart + lebBytes + KeyLength)`,
   where `KeyLength` comes from the BTree trailer (the value is the same
   for every entry in this HSST).

**Why `MetadataStart` aims at `ValueLength` and not at the value.** Values
are unbounded (KiB–MiB, including nested HSSTs) so `ValueLength` is LEB128.
LEB128 has a forward-only terminator (high-bit "continuation" chain): given
a byte mid-stream you can't tell whether you're inside someone else's
continuation run or sitting at the start of a fresh varint. So the format
places the length *after* the value and aims the index pointer at it; the
value is back-derived from `MetadataStart - ValueLength`. `FullKey` is
forward-decoded after that, using the trailer's `KeyLength`. This is a
load-bearing invariant — the entry tail must keep `MetadataStart` as the
value↔length pivot.

**Separator vs. full key.** The leaf B-tree node *also* stores a
**separator** for each entry — a min-length prefix chosen against the
entry's neighbours, used purely to drive in-leaf binary search. The
data-region entry is self-describing (carries the full key), so a reader
doesn't need to combine separator + suffix; it can decode the full key
directly from the entry tail. This costs `separator.Length` extra bytes
per entry (the prefix is duplicated) in exchange for: simpler decoding,
no per-entry key reconstruction during iteration, and entries that can be
recovered from just `(buffer, MetadataStart)` without consulting any
index.

### PackedArray variant

A specialised layout for fixed-size keys and values. The b-tree is replaced
by a packed entry array with a recursive "summary" index and an optional
hash table.

```
[Data][Summary L0]…[Summary L(D-1)][HashTable: 4·TableSize bytes (omitted when 0)][Metadata][MetadataLength: u8][IndexType: u8 = 0x02]
```

- **`Data`** — `EntryCount * (KeySize + ValueSize)` bytes, packed. Each entry
  is `[Key: KeySize bytes][Value: ValueSize bytes]`. Entries are stored in
  strictly ascending key order; random access by entry index is just a
  multiply (`offset = i * (KeySize + ValueSize)`). Both `KeySize` and
  `ValueSize` are immutable per HSST and read from `Metadata`.
- **`Summary L0..L(D-1)`** — `Depth` levels of summary, each a contiguous
  array of `Count_k` records of just `[CheckpointKey: KeySize bytes]` —
  no per-record index field. Slab boundaries are derived from position
  alone, using the strides recorded in `Metadata`:
  - **Level 0** indexes into `Data` with stride
    `N = 1 << EntriesPerCkLevel0Log2`: the builder emits a checkpoint
    after every `N`-th data entry, plus a final tail checkpoint when
    `EntryCount & (N-1) != 0`. `N` is always a power of two so the reader
    uses a mask + shift instead of div/mod. The checkpoint key at index
    `i` is the key of the last data entry it covers — i.e. data index
    `min((i+1)*N - 1, EntryCount - 1)`.
  - **Level k+1** indexes into level k with stride
    `M = 1 << RecordsPerCkHigherLog2` (also a power of two, ≥ 2 when used):
    same scheme over the `Count_k` records of level k.
  - Levels are stored in order on disk (Level 0 closest to `Data`, Level
    `Depth-1` closest to `HashTable`/`Metadata`). The builder stops adding
    levels once a level would produce ≤ 1 record.
  - `Depth = 0` is legal — for tiny HSSTs the data range is searched
    directly.
- **`HashTable`** — Optional. When `TableSize == 0` the section is omitted
  entirely (no on-disk bytes). When present, `TableSize` `u32` LE slots;
  `0x00000000` = empty, `0xFFFFFFFF` = collision sentinel, otherwise the
  slot stores `entryIndex + 1` (1-based). Hash function is the low 32 bits
  of `XxHash3` over the full key bytes; the slot is derived via Lemire's
  multiply-shift reduction
  `(uint)(((ulong)hash * (ulong)TableSize) >> 32)` so `TableSize` need not
  be a power of two.
- **`Metadata`** — sequence of LEB128 varints, read forward from
  `metaAbsStart = hsstEnd - 2 - MetadataLength`:
  ```
  [KeySize][ValueSize][EntryCount][TableSize][EntriesPerCkLevel0Log2][RecordsPerCkHigherLog2][Depth][Count_0]…[Count_{Depth-1}]
  ```
  `TableSize == 0` signals "no hash table"; `Depth` is capped at 8.
  `RecordsPerCkHigherLog2` must be ≥ 1 when `Depth >= 2`; for `Depth ≤ 1`
  it is ignored on read but still written.

**Lookup procedure** (exact and floor):

1. **Hash fast path.** When `TableSize > 0` and `key.Length == KeySize`,
   compute `slot = (uint)(((ulong)HashKey(key) * (ulong)TableSize) >> 32)`.
   On `entryIdx+1`, read the candidate from `Data` and compare; on match
   return; on mismatch + exact → not found; otherwise fall through. Empty
   slot on exact → not found; on floor fall through. Collision → fall
   through.
2. **Recursive summary descent.** Maintain a slab `[lo, hi]` of records at
   the current level. Start at level `Depth-1` with the full range
   `[0, Count_{Depth-1} - 1]`. Binary-search the slab for the smallest ck
   index `c` whose key is `≥ target`. If none exists in the slab, set
   `c = hi` (floor) or return "not found" (exact). The slab at the level
   below is `[c*stride, min((c+1)*stride - 1, parentCount - 1)]`, where
   `stride = N` if descending into `Data` (level 0 → data), else
   `stride = M`, and `parentCount = EntryCount` or `Count_{k-1}`.
3. **Data binary search.** Binary-search the level-0 slab for the smallest
   entry whose key is `≥ target`. If equal, return; for floor on a miss
   return entry at `insertionPoint − 1` (the data array is globally sorted,
   so going outside the slab is safe).

**Restrictions and trade-offs.**

- Every key must be exactly `KeySize` bytes; every value exactly
  `ValueSize` bytes. The format rejects mismatches at build time.
- `MetadataLength` is a single byte — metadata is small, so this never
  binds in practice.
- Per-entry overhead is zero (no LEB128 length prefixes, no per-entry
  metadata pointer); summary overhead is `KeySize` bytes per checkpoint
  (no `LastEntryIndex` field — slab bounds are derived from position),
  plus a geometrically smaller cost from higher levels, plus the optional
  hash table.
- Random access by entry index is `O(1)`; lookups are
  `O(Depth · log(stride/KeySize) + log N)` reads of `KeySize` bytes each.

### DenseByteIndex variant

A single-byte-keyed map where the tag byte *is* the array index — no
`Tags` array. The reader resolves single-byte key `k` directly to
`Ends[k]` with no scan. Used for column containers where the set of tag
positions is fixed and known (persisted-snapshot outer column container;
per-address sub-tag container).

```
[Value_0][Value_1]…[Value_{N-1}][Ends: N·u32 LE][Count: u8 = N − 1][IndexType: u8 = 0x04]
```

- **`Value_i`** — raw bytes of the value associated with tag `i`. Tag
  positions that were never written are gap-filled with **zero-length**
  values: `Ends[i] == (i == 0 ? 0 : Ends[i-1])`. Length 0 is therefore
  the in-band "absent" marker — callers that need to distinguish absent
  from present-but-empty must encode a presence byte inside the value.
- **`Ends`** — `N` little-endian `u32`s. `Ends[i]` is the exclusive end
  offset of `Value_i` measured from byte 0 of the HSST. `N` is
  `(highestWrittenTag + 1)`.
- **`Count`** — single byte, holds `N − 1` (so `N` ranges over `1..256`
  encoded as `0..255`). The empty case (no values ever written) is not
  representable; callers must always emit at least one entry.

**Lookup procedure** (exact and floor):

1. Read tail byte → `IndexType` must equal `0x04`.
2. Read byte at `end - 2` → `N − 1`; `N = (Count) + 1`.
3. Reject lookups whose key is not exactly 1 byte. For exact match,
   reject keys with `key[0] >= N`. For floor, clamp `k = min(key[0], N - 1)`.
4. `Ends` lives at `[end - 2 - 4·N, end - 2)`. Read `Ends[k]` (and
   `Ends[k-1]` when `k > 0`) to derive `valueStart`/`valueEnd`. A
   zero-length result on exact match means absent → not found; on floor
   the reader walks down to the largest `j ≤ k` with non-zero length.

**Restrictions and trade-offs.**

- All keys are exactly 1 byte. Multi-byte keys are rejected at build time.
- `N ≤ 256` (`Count` is a u8 holding `N − 1`).
- Densest single-byte-keyed encoding (no `Tags` array, no scan); strictly
  worse when most tag positions are unused (gap-filled `Ends` slots are
  paid in full).

## B-tree index node layout

Each node (root, intermediate, or leaf) ends with a trailing `MetadataLength`
byte. Reading an index node backward from its exclusive-end offset:

```
[Values section][Keys section][Metadata][MetadataLength: u8]
                                                          ^
                                                          end of node
```

### Metadata

```
[Flags: u8][KeyCount: LEB128][KeySize: LEB128][ValueSize: u8][BaseOffset: 6 bytes LE][CommonKeyPrefixLen: u8 + bytes optional]
```

`ValueSize` is a single byte because per-entry value slots are 1..8 bytes
(Uniform pointers); the b-tree index nodes never use Variable-encoded value
sections.

`BaseOffset` is a **mandatory** fixed 6-byte little-endian unsigned integer
(low 48 bits; enough for any HSST up to 256 TiB). The 6 bytes are paid once
per node, and per-entry slot widths are picked from `[1, 8]` to keep the
total cheaper than always-4-byte slots. There is no flag bit gating it.

`Flags` bits:

| Bit  | Meaning |
|------|---------|
| 0    | `IsIntermediate` — 1 = intermediate B-tree node, 0 = leaf |
| 1–2  | `KeyType`        — 0 Variable / 1 Uniform / 2 UniformWithLen |
| 3–4  | `ValueType`      — 0 Variable / 1 Uniform / 2 UniformWithLen |
| 5    | reserved (was `HasBaseOffset`; BaseOffset is now mandatory). Writers MUST emit 0; readers MUST ignore. |
| 6    | `HasCommonKeyPrefix` — 1 = `CommonKeyPrefixLen` (u8) + prefix bytes follow |
| 7    | `HasFlagsContinuation` — 1 = a second flags byte follows the first, reserved for future expansion. Current writers always emit 0; current readers may reject `1` as unsupported. |

When `HasCommonKeyPrefix` is set, every stored key in the node equals
`CommonKeyPrefix || suffix_i` where `suffix_i` is what the keys section
encodes. `KeySize` / slot semantics apply to the *suffixes* — `Uniform` slot
size is `commonSuffixLen`, `UniformWithLen` slot is `maxSuffixLen + 1`,
`Variable` section size covers only suffix LEB-prefixed bytes plus the
offset table. The prefix bytes live entirely inside metadata; section size
math is unchanged. Writers cap the prefix at **128 bytes** so the metadata
stays well under the `MetadataLength` u8 ceiling, and only emit it when
`prefixLen × (count − 1) > 1` (i.e. it strictly pays back its
`1 + prefixLen` overhead) and when at least one suffix is non-empty.

`KeySize` / `ValueSize` semantics depend on the corresponding type:

- **Variable (0)** — the value of `KeySize`/`ValueSize` is the *section's*
  total byte size. The section holds `LEB128 length || bytes` per entry at
  the front, followed by a `KeyCount * 2`-byte little-endian offset table at
  the **end** of the section. Offsets are relative to the section's start
  (i.e. the first entry sits at offset 0). The maximum addressable section
  data region is therefore 64 KiB; the writer rejects nodes that would
  exceed it.
- **Uniform (1)** — packed fixed-width entries. Each entry is exactly
  `KeySize` (or `ValueSize`) bytes; section size is `KeyCount * size`.
- **UniformWithLen (2)** — fixed slot size, but the last byte of each slot
  records the actual byte length used. Section size still `KeyCount * size`.

`BaseOffset`, when present, is added to every integer value read out of the
node. The writer picks `BaseOffset = min(values)` (when there's more than one
distinct value and the minimum is non-zero) and then stores each value as a
**Uniform unsigned LE integer** whose width is the smallest power-of-two-byte
count in `[1, 8]` that fits `max(values) - BaseOffset`. The chosen width is
recorded in the node header's `ValueSize` field, so a leaf with deltas that
all fit in one byte stores 1-byte slots, while a leaf spanning a 5 GiB
range stores 5-byte slots.

### Children pointers (intermediate nodes)

For an intermediate node, each value is a 1..8 byte little-endian unsigned
integer (Uniform; the byte width comes from `ValueSize`) interpreted (after
`+ BaseOffset`) as the **inclusive last byte** of the referenced child node
within the HSST buffer (0-indexed from the first byte of the HSST). The
child's exclusive end = `childOffset + 1`; the reader then loads the child
from the end the same way it loaded the root.

### Metadata-start pointers (leaves)

For a leaf node, each value is a 1..8 byte little-endian unsigned integer
(after `+ BaseOffset`) giving the entry's `MetadataStart`, *relative to the
start of the data region* (i.e. byte 0 of the HSST is the first byte of the
data region).

## Constraints

- Maximum entries per leaf node: **64** by default; configurable at write
  time. Beyond that, the writer splits the leaf and promotes a separator
  into an intermediate node.
- Maximum key length per entry: **255 bytes**. Every entry in a BTree HSST
  shares the same key length, recorded once in the trailer as a single `u8`
  (so 0–255). Writers must reject longer keys and reject mid-build key-length
  changes.
- `MetadataLength` is a single byte → metadata section ≤ 255 bytes.
- Per-entry value slots are 1..8 byte LE unsigned integers (width per
  `ValueSize`). Combined with the optional 6-byte `BaseOffset`, a single
  HSST can address up to 256 TiB. The variable-section internal offset
  table (Variable key/value sections) remains a `u16` per entry, so a
  single Variable section is still capped at 64 KiB. There is no in-format
  cap on a containing host file holding many HSSTs.
## Affected files

When changing this format, every file below has byte-level knowledge of
the layout and must be reviewed in lockstep with this document. If you
add a new file that encodes or decodes HSST bytes, append it here.

Writers / encoders:
- `Hsst/HsstBTreeBuilder.cs` — top-level HSST builder; writes the data region,
  drives the index builder, appends the trailing `IndexType` byte.
- `Hsst/HsstIndexBuilder.cs` — drives B-tree shape (leaf splitting,
  intermediate-node promotion).
- `Hsst/HsstIndexNodeWriter.cs` — writes a single index node's bytes
  (`Values | Keys | Metadata | MetadataLength`).
- `BSearchIndex/BSearchIndexWriter.cs` — alternate node writer used by
  the merge path; must stay byte-compatible with `HsstIndexNodeWriter`.
- `BSearchIndex/BSearchIndexLayoutPlanner.cs` — picks key/value section
  encodings (Variable / Uniform / UniformWithLen) and section sizes.
- `Hsst/IndexType.cs` — enum of valid index-type byte values.
- `Hsst/HsstPackedArrayBuilder.cs` / `Hsst/HsstPackedArrayReader.cs` — `PackedArray`
  writer / reader (recursive summary index, optional hash table).
- `Hsst/HsstDenseByteIndexBuilder.cs` — `DenseByteIndex` writer
  (concatenated values + Ends-only trailer; tag-byte = array index).

Readers / decoders:
- `Hsst/HsstReader.cs` — point-query reader; reads the trailing
  `IndexType` byte and walks the B-tree from the tail.
- `Hsst/HsstIndex.cs` — parses a single index node from its tail.
- `BSearchIndex/BSearchIndexReader.cs` — alternate index-node decoder
  used by the merge path; mirrors `HsstIndex` parsing.
- `Hsst/HsstDenseByteIndexReader.cs` — `DenseByteIndex` lookup helper
  (direct `Ends[k]` index, no tag scan); dispatched into from
  `HsstReader`.
- `Hsst/HsstPackedArrayReader.cs` — `PackedArray` lookup helper
  (recursive summary descent + optional hash fast path).

Iterators:
- `Hsst/HsstEnumerator.cs` — forward iterator over a whole HSST scope;
  reads the trailing `IndexType` byte, descends to the leftmost leaf,
  and walks key-sorted entries via end-anchored ancestor frames.
- `Hsst/HsstMergeEnumerator.cs` — N-way-merge cursor; collects every
  leaf entry's `(separator, metaStart)` up-front so a
  sort-merge can round-robin many cursors without per-step allocations.

Size / capacity math:
- `PersistedSnapshots/HsstSizeEstimator.cs` — every constant here
  (minimum HSST size, per-entry overhead, per-leaf overhead) tracks the
  bytes the builder actually emits. Update whenever the wire layout
  gains or loses bytes.

Tests that pin the wire format (rename / re-anchor when bytes move):
- `Nethermind.State.Flat.Test/Hsst/HsstTests.cs` —
  `IndexType_Byte_Is_BTree_At_Tail` and round-trip tests.
- `Nethermind.State.Flat.Test/Hsst/HsstReaderTests.cs` —
  `IndexType_Byte_Is_BTree_ReaderWorks`.
- `Nethermind.State.Flat.Test/BSearchIndex/BSearchIndexTests.cs` — hex
  fixture tests for individual index nodes; `ReadFromEnd(data, …)` call
  sites are sensitive to where the trailing byte sits.
