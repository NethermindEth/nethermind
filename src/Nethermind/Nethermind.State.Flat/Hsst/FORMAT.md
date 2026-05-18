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
| **BTree** | `[Data Region (entries + inline page-local leaves)][Index Region (intermediates only)][RootPrefix: RootPrefixLen bytes][RootPrefixLen: u8][RootSize: u16 LE][KeyLength: u8][IndexType: u8 = 0x01]` |
| **PackedArray** | `[Data][Summary L0]…[Summary L(D-1)][Metadata: 10 bytes][MetadataLength: u8 = 10][IndexType: u8 = 0x02]` |
| **DenseByteIndex** | `[Value_{N-1}]…[Value_0][Ends: N·OffsetSize LE][Count: u8 = N − 1][OffsetSize: u8][IndexType: u8 = 0x04]` (values laid down high-tag-first; `OffsetSize ∈ {1, 2, 4, 6}`) |
| **TwoByteSlotValue** | `[KeyCount: u16 LE = N − 1][Key_0: 2 bytes]…[Key_{N-1}: 2 bytes][Offset_1: u16 LE]…[Offset_{N-1}: u16 LE][Value_0]…[Value_{N-1}][IndexType: u8 = 0x05]` |
| **TwoByteSlotValueLarge** | `[KeyCount: u16 LE = N − 1][Key_0: 2 bytes]…[Key_{N-1}: 2 bytes][Offset_1: u24 LE]…[Offset_{N-1}: u24 LE][Value_0]…[Value_{N-1}][IndexType: u8 = 0x06]` |
| **BTreeKeyFirst** | `[Data Region (key-first entries + inline page-local leaves)][Index Region (intermediates only)][RootPrefix: RootPrefixLen bytes][RootPrefixLen: u8][RootSize: u16 LE][KeyLength: u8][IndexType: u8 = 0x07]` |

The trailing **index type byte** is the last byte of the HSST and selects
the variant by enumerated value (not a bitfield):

| Value | Name | Meaning |
|---|---|---|
| `0x01` | `BTree` | Separate data region; leaves hold metaStart pointers aimed at the per-entry LEB128 length byte (key-after-value entry layout). Fixed key length recorded once in the trailer rather than per entry. The root's common-key-prefix bytes ride in the trailer (`RootPrefix`) — per-node headers store only `CommonPrefixLen`; non-root nodes inherit the prefix bytes from the parent's separator during descent, but the root has no parent, so its bytes sit in the trailer. |
| `0x02` | `PackedArray` | Fixed-size key/value array with a recursive "summary" index. (Earlier revisions of the format carried an optional open-addressed hash table; that section has been removed.) |
| `0x03` | _reserved_ | Previously `ByteTagMap`; do not reuse without bumping the wire format. |
| `0x04` | `DenseByteIndex` | Single-byte-keyed map indexed directly by the tag byte; gap-filled with zero-length values. |
| `0x05` | `TwoByteSlotValue` | Fixed 2-byte key map; keys-first wire shape (KeyCount header, then keys, then offsets, then values, then IndexType). First offset omitted (always 0); cumulative values capped at 65,535 bytes by u16 offsets. |
| `0x06` | `TwoByteSlotValueLarge` | Identical shape to `TwoByteSlotValue` but u24 LE offsets, raising the values-section cap to ~16 MiB. Picked when the u16 sibling can't fit the payload. |
| `0x07` | `BTreeKeyFirst` | Same overall layout as `BTree` but per-entry bytes are key-first (`[FullKey][LEB128 ValueLength][Value]`) and leaves hold pointers to the FullKey byte 0 (EntryStart). Selected by callers whose values are large nested HSSTs so the outer entry's metadata sits at the entry's front, parallel to the inner HSST's keys-first layout. Same root-prefix-in-trailer convention as `0x01`. |

Other values are reserved for future index strategies. The root B-tree node
lives just before the BTree trailer
(`[RootPrefix bytes][RootPrefixLen u8][RootSize u16 LE][KeyLength u8][IndexType u8]`,
totalling `5 + RootPrefixLen` bytes) and is located by computing
`root_start = HSST_end - 5 - RootPrefixLen - RootSize`.

### BTree variant

The BTree HSST stores a fixed key length per blob: every entry in the
table has a key of exactly `KeyLength` bytes (0–255), recorded once in the
trailer's `KeyLength: u8` field. The data region is a packed sequence of
variable-length, **self-describing** entries laid out value-first so that
decoding is forward-readable from a known `MetadataStart` cursor:

```
[Value: V bytes][FlagByte][ValueLength: LEB128][FullKey: KeyLength bytes]
                ^
                MetadataStart  (= the index pointer's target byte)
```

`MetadataStart` is the byte offset (within the HSST buffer, measured from
byte 0 — the first byte of the data region) of the entry's **leading flag
byte**. The flag byte's low 2 bits encode the `BSearchNodeKind` (Entry,
Leaf, or Intermediate) — the same flag-byte layout used by `BSearchIndex`
node headers — so the BTree reader's dispatch loop can recognize *what
kind of thing it just landed on* from a single byte read. For entries the
flag is `NodeKind = Entry (00)`; bits 2–7 are reserved and written as
zero. The leaf B-tree node stores `MetadataStart` for every entry; readers
seek into the leaf, take the metaStart pointer, then:

1. Read the 1-byte flag at `MetadataStart`. The low 2 bits must be
   `NodeKind = Entry`; the dispatch loop terminates here for the
   target entry (Leaf and Intermediate kinds route through
   `BSearchIndexReader.ReadFromStart` instead).
2. Decode `ValueLength` (LEB128) starting at `MetadataStart + 1` — the
   value bytes live at `[MetadataStart - ValueLength, MetadataStart)`.
3. The full key sits at
   `[MetadataStart + 1 + lebBytes, MetadataStart + 1 + lebBytes + KeyLength)`,
   where `KeyLength` comes from the BTree trailer (the value is the same
   for every entry in this HSST).

**Page-local leaves.** Leaf `BSearchIndex` nodes are emitted *inline in
the data region*, next to the entries they describe, not in a separate
trailing index region. The builder fires a leaf write whenever adding the
next entry would push the (pending-entries + estimated-leaf) layout past
the current 4 KiB page boundary, and again at `Build()` start for any
tail entries. The result is that the leaf and most of its entries land in
the same 4 KiB page — a seek for a small entry that's already pulled the
page into cache reaches the value without a second I/O.

The `BSearchIndex` node's flag byte (bits 0-1 = `NodeKind = Leaf` for
these) is the same flag byte that the reader's dispatch loop reads — so
landing on either an entry-flag or a leaf-flag is uniform from the
loop's point of view. **Variable depth** falls out of this: some
subtrees stop at a leaf (one level above the entry), others (when the
trigger left a singleton pending) stop with an intermediate pointing
directly at the entry. Today's naive trigger always emits a leaf even
for singletons, so on-disk the tree shape stays leaf-at-bottom; the
format permits direct-entry children for a future trigger that wants
to skip the singleton-leaf cost.

**Trailer.** The HSST tail is
`[RootPrefix bytes][RootPrefixLen: u8][RootSize: u16 LE][KeyLength: u8][IndexType: u8]`,
totalling `5 + RootPrefixLen` bytes. `RootSize` locates the root B-tree
node via `root_start = HSST_end − 5 − RootPrefixLen − RootSize`.
`RootPrefixLen` and the preceding `RootPrefix` bytes carry the root's
`CommonKeyPrefix` — the per-node header stores only `CommonPrefixLen`, not
the prefix bytes, because non-root nodes receive their prefix bytes from
the parent's separator during descent; the root has no parent, so the
bytes ride the trailer instead. `KeyLength` is the fixed key length every
entry in this HSST uses (0..255), recorded once; `KeyLength = 0` when the
HSST was built empty.

**Why `MetadataStart` aims at `ValueLength` and not at the value.** Values
are unbounded (KiB–MiB, including nested HSSTs) so `ValueLength` is LEB128.
LEB128 has a forward-only terminator (high-bit "continuation" chain): given
a byte mid-stream you can't tell whether you're inside someone else's
continuation run or sitting at the start of a fresh varint. So the format
places the length *after* the value and aims the index pointer at it; the
value is back-derived from `MetadataStart - ValueLength`. `FullKey` is
forward-decoded after that, using the trailer's `KeyLength`. This is a
load-bearing invariant for this variant — the entry tail must keep
`MetadataStart` as the value↔length pivot. The `BTreeKeyFirst` variant
(0x07) flips this for callers whose values are large nested HSSTs and want
the entry's metadata at the entry's front instead; see that section below.

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

### BTreeKeyFirst variant

`BTreeKeyFirst` (IndexType `0x07`) uses the same top-level layout as
`BTree` — data region followed by an index region followed by the
`[RootPrefix bytes][RootPrefixLen: u8][RootSize: u16 LE][KeyLength: u8][IndexType: u8]`
trailer (`5 + RootPrefixLen` bytes, located via
`root_start = HSST_end − 5 − RootPrefixLen − RootSize`) — and the same
index node format (the index region itself is bit-for-bit identical).
`RootPrefix` carries the root node's common-key-prefix bytes for the same
reason as in `BTree` (see that section). Only the per-entry data-region
bytes are reshaped:

```
[FlagByte][FullKey: KeyLength bytes][ValueLength: LEB128][Value: V bytes]
^
EntryStart  (= the index pointer's target byte)
```

`EntryStart` is the byte offset (within the HSST buffer, measured from
byte 0) of the entry's leading flag byte (same flag-byte convention as
the `BTree` variant — `NodeKind = Entry (00)` in bits 0-1, bits 2-7
reserved zero). The leaf B-tree node stores this offset for every entry;
readers take the pointer, read the flag byte, then walk forward:

1. The full key sits at `[EntryStart + 1, EntryStart + 1 + KeyLength)`,
   where `KeyLength` comes from the trailer.
2. Decode `ValueLength` (LEB128) starting at `EntryStart + 1 + KeyLength`.
3. The value bytes live at `[EntryStart + 1 + KeyLength + lebBytes,
   EntryStart + 1 + KeyLength + lebBytes + ValueLength)`.

**Why a separate variant.** With the key at the entry's front the entry's
per-entry metadata (FullKey + LEB128 length) is contiguous at the start
of the entry. When the value is itself a keys-first nested HSST (e.g. a
`TwoByteSlotValue` sub-slot whose KeyCount sits at byte 0 of the inner
blob), the outer entry's metadata and the inner HSST's metadata both
appear at the front of their respective scopes — a forward scan crossing
the boundary walks key → length → inner-metadata → inner-keys →
inner-offsets → inner-values without any backward seeks. Selected by
callers whose values are large nested HSSTs; non-slot BTrees keep `0x01`
(the streaming-write API requires the value bytes before the value
length, so it cannot lay down a forward `ValueLength` LEB128 without
buffering — `BTreeKeyFirst` therefore requires `Add(key, valueSpan)` and
rejects the `BeginValueWrite` / `FinishValueWrite` streaming API).

**Separator vs. full key.** Same as `BTree`: the leaf node carries a
short separator for in-leaf binary search, while the data-region entry
remains self-describing. No reader has to consult both at once — exact
matches verify by reading the full key from `EntryStart` directly.

### PackedArray variant

A specialised layout for fixed-size keys and values. The b-tree is replaced
by a packed entry array with a recursive "summary" index.

```
[Data][Summary L0]…[Summary L(D-1)][Metadata: 10 bytes][MetadataLength: u8 = 10][IndexType: u8 = 0x02]
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
    `Depth-1` closest to `Metadata`). The builder stops adding levels once
    a level would produce ≤ 1 record.
  - `Depth = 0` is legal — for tiny HSSTs the data range is searched
    directly.
- **`Metadata`** — fixed 10-byte struct (no LEB128), read forward from
  `metaAbsStart = hsstEnd - 2 - MetadataLength`:
  ```
  [KeySize: u8][ValueSize: u8][EntryCount: u32 LE][EntriesPerCkLevel0Log2: u8][RecordsPerCkHigherLog2: u8][Depth: u8][Flags: u8]
  ```
  `Flags` bit 0 = `IsLittleEndian` (only valid when `KeySize ∈ {2,4,8}`;
  when set, every stored key — data and summary — is byte-reversed so an
  x86 LE integer load recovers lex order, matching the BSearchIndex
  LE-stored convention and unlocking the AVX-512 floor-scan fast path).
  Other Flags bits are reserved (must be 0). `Depth` is capped at 8.
  `RecordsPerCkHigherLog2` must be ≥ 1 when `Depth ≥ 2`; for `Depth ≤ 1`
  it is ignored on read but still written. Per-level record counts
  `Count_k` are **not stored** — the reader derives them from `EntryCount`
  and the strides (`Count_0 = ceil(EntryCount / N)`,
  `Count_{k+1} = ceil(Count_k / M)`).
- **`MetadataLength`** is always 10 for this format revision. It is kept as
  a single byte so the reader can locate `Metadata` consistently if the
  struct is ever widened.

**Lookup procedure** (exact and floor):

1. **Recursive summary descent.** Maintain a slab `[lo, hi]` of records at
   the current level. Start at level `Depth-1` with the full range
   `[0, Count_{Depth-1} - 1]`. Binary-search the slab for the smallest ck
   index `c` whose key is `≥ target`. If none exists in the slab, set
   `c = hi` (floor) or return "not found" (exact). The slab at the level
   below is `[c*stride, min((c+1)*stride - 1, parentCount - 1)]`, where
   `stride = N` if descending into `Data` (level 0 → data), else
   `stride = M`, and `parentCount = EntryCount` or `Count_{k-1}`.
2. **Data binary search.** Binary-search the level-0 slab for the smallest
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
  plus a geometrically smaller cost from higher levels.
- Random access by entry index is `O(1)`; lookups are
  `O(Depth · log(stride/KeySize) + log N)` reads of `KeySize` bytes each.

### DenseByteIndex variant

A single-byte-keyed map where the tag byte *is* the array index — no
`Tags` array. The reader resolves single-byte key `k` directly to
`Ends[k]` with no scan. Used for column containers where the set of tag
positions is fixed and known (persisted-snapshot outer column container;
per-address sub-tag container).

```
[Value_{N-1}][Value_{N-2}]…[Value_0][Ends: N·OffsetSize LE][Count: u8 = N − 1][OffsetSize: u8][IndexType: u8 = 0x04]
```

The values region is stored in **strictly descending tag order** — the
lowest written tag's bytes sit immediately before `Ends` so that the
hottest small-blob entries share OS pages with the lookup-time trailer.
`Value_0` (lowest tag) sits adjacent to `Ends`; `Value_{N-1}` (highest
written tag) starts at byte 0 of the HSST.

- **`Value_i`** — raw bytes of the value associated with tag `i`. Tag
  positions that were never written are gap-filled with **zero-length**
  values: their `Ends[i]` reuses the exclusive end of the next-higher
  in-array tag, so `Ends[i] − Ends[i + 1]` collapses to `0`. Below-range
  positions `[0, _lastTag)` (entries below the lowest written tag) are
  filled the same way at build time. Length 0 is therefore the in-band
  "absent" marker — callers that need to distinguish absent from
  present-but-empty must encode a presence byte inside the value.
- **`Ends`** — `N` little-endian unsigned integers of width
  `OffsetSize ∈ {1, 2, 4, 6}` (chosen at build time to fit the cumulative
  values total). `Ends[i]` is the exclusive end offset of `Value_i`
  measured from byte 0 of the HSST. Because higher tags are written
  first, `Ends` is monotonically **non-increasing** with `i`. The highest
  in-array tag (`i = N − 1`) was the first written and starts at offset
  0, so its implicit `prevEnd` is 0. `N` is `(highestWrittenTag + 1)`.
- **`Count`** — single byte, holds `N − 1` (so `N` ranges over `1..256`
  encoded as `0..255`). The empty case (no values ever written) is not
  representable; callers must always emit at least one entry.
- **`OffsetSize`** — single byte sitting between `Count` and `IndexType`,
  carrying the per-end-slot byte width. Restricted to `{1, 2, 4, 6}`.

**Lookup procedure** (exact and floor):

1. Read tail byte → `IndexType` must equal `0x04`.
2. Read bytes at `[end − 3, end − 1)` → `Count: u8` and `OffsetSize: u8`;
   `N = Count + 1`.
3. Reject lookups whose key is not exactly 1 byte. For exact match,
   reject keys with `key[0] >= N`. For floor, clamp `k = min(key[0], N − 1)`.
4. `Ends` lives at `[end − 3 − N·OffsetSize, end − 3)`. Derive
   `prevEnd = (k == N − 1 ? 0 : Ends[k + 1])` and `thisEnd = Ends[k]`;
   the value occupies `[prevEnd, thisEnd)` measured from byte 0 of the
   HSST, and `valueLen = thisEnd − prevEnd`. A zero-length result on
   exact match means absent → not found; on floor the reader walks down
   to the largest `j ≤ k` with non-zero length.

**Restrictions and trade-offs.**

- All keys are exactly 1 byte. Multi-byte keys are rejected at build time.
- `N ≤ 256` (`Count` is a u8 holding `N − 1`).
- Densest single-byte-keyed encoding (no `Tags` array, no scan); strictly
  worse when most tag positions are unused (gap-filled `Ends` slots are
  paid in full).

### TwoByteSlotValue variant

A fixed 2-byte key map with variable values, a keys-first wire shape, and
a contiguous sorted key array. Designed for the inner slot-suffix HSST
(2-byte slot-suffix → 0..32-byte slot value) where the cumulative values
are small enough to encode every start offset in a single `u16`. Keys and
the offsets section sit ahead of the values so a forward scan touches the
metadata that drives the lookup before reaching the bulk value bytes —
the hardware prefetcher and cache-line layout favor this order.

```
[KeyCount: u16 LE = N − 1][Key_0: 2 bytes]…[Key_{N-1}: 2 bytes][Offset_1: u16 LE]…[Offset_{N-1}: u16 LE][Value_0][Value_1]…[Value_{N-1}][IndexType: u8 = 0x05]
```

- **`KeyCount`** — `u16` LE holding `N − 1`, so the range `1..65536` fits.
  Sits at byte 0 of the HSST so the reader can locate keys / offsets /
  values without reading from the tail first.
- **`Key_i`** — 2 bytes, **byte-reversed** from the caller's input
  (LE-stored). A native `u16` load over a stored key recovers the original
  BE-numeric value, so unsigned `u16` compare on the loaded value matches
  lex byte compare on the input — supporting SIMD scans of 8/16/32 keys
  per iteration. Keys are strictly ascending in caller (lex/BE) order
  across `i`. Matches the `PackedArray` LE-stored convention for 2-byte
  keys.
- **`Offset_i`** — exclusive **start** offset of `Value_i`, measured from
  the *start of the values section* (= byte after the last offset).
  `Offset_0` is omitted because it is always `0`. `Offset_N`
  (one-past-end of the values section) is not stored; the reader derives
  it from `HSSTLength − 1` (i.e. the byte before the trailing IndexType
  byte), so `Value_i` occupies `[Offset_i, Offset_{i+1})` within the
  values section with `Offset_0 = 0` implicit.
- **`Value_i`** — raw bytes of the value associated with `Key_i`. Length is
  derived from adjacent offsets; 0-length is legal and is the in-band
  "absent / deleted" marker.
- **`IndexType`** — single byte at the tail (`0x05`). The HSST reader
  dispatches on the last byte; the rest of the metadata lives at the
  front.

**Header + non-value overhead** = `2 + N·2 + (N − 1)·2 + 1 = 4N + 1`
bytes (same total as the pre-rewrite tail-metadata layout — only the
ordering changed). Total HSST size = `4N + 1 + ∑|Value_i|`.

**Builder buffering.** Because the offsets section sits *before* the
values section, the writer must know every value's length up front. The
builder therefore copies value bytes into pooled scratch during `Add()`
and flushes the whole keys / offsets / values block in `Build()`; the
streaming `BeginValueWrite`/`FinishValueWrite` API is not offered for
this variant. With the 64 KiB cap on cumulative values, the staging cost
is small and well below the working-set budget callers already accept.

**Lookup procedure** (exact and floor):

1. Read tail byte → `IndexType` must equal `0x05`.
2. Read 2 bytes at byte 0 → `KeyCount` u16 LE → `N = KeyCount + 1`.
3. Reject lookups whose key length is not exactly 2.
4. Keys array lives at `[2, 2 + 2·N)`. Binary-search the array for the
   smallest index `i` whose key is `≥ target`.
5. On exact match — return `Value_i`. On miss with exact-lookup → not
   found. On miss with floor lookup → return `Value_{i-1}` (or not-found
   when `i == 0`).
6. Compute `valuesStart = 2 + 2·N + 2·(N − 1)` and
   `valuesEnd = HSSTLength − 1`. Resolve `Value_i`'s bound from
   `Offset_i` (= 0 when `i == 0`, else read `u16` LE at
   `offsetsStart + 2·(i − 1)`) and `Offset_{i+1}` (= `valuesEnd −
   valuesStart` when `i == N − 1`, else read `u16` LE at
   `offsetsStart + 2·i`).

**Restrictions and trade-offs.**

- All keys are exactly 2 bytes. Multi-byte/empty keys are rejected at
  build time.
- The cumulative values are capped at `ushort.MaxValue` (65,535 bytes)
  by the u16 offset width. Builders reject overflow at `Add` time;
  callers gate on a size check or fall back to the `0x06` sibling.
- `N ≤ 65536` (`KeyCount` is a u16 holding `N − 1`).
- Per-entry overhead is `2` (key) `+ 2` (offset; except for the omitted
  `Offset_0`) bytes; no LEB128, no metadata pointer, no separator.
  Lookups are one binary search over `2N` contiguous bytes plus at most
  two `u16` reads to resolve the value bound.

### TwoByteSlotValueLarge variant

Identical layout to `TwoByteSlotValue` but with `u24` (3-byte LE) start
offsets, raising the values-section cap from 64 KiB to ~16 MiB. Picked
when the cumulative payload for a slot-suffix group exceeds the u16
sibling's cap.

```
[KeyCount: u16 LE = N − 1][Key_0: 2 bytes]…[Key_{N-1}: 2 bytes][Offset_1: u24 LE]…[Offset_{N-1}: u24 LE][Value_0][Value_1]…[Value_{N-1}][IndexType: u8 = 0x06]
```

- **`Offset_i`** — `u24` LE start offset (low 3 bytes of a `u32`),
  values-section-relative. `Offset_0` is omitted; `Offset_N` is derived
  as `HSSTLength − 1 − valuesStart`. Value `i` spans `[Offset_i,
  Offset_{i+1})` within the values section.
- All other fields (`KeyCount`, `Key_i`, `IndexType`) match the u16
  sibling exactly, including the LE-stored 2-byte key convention, the
  strict-ascending byte-lex order on caller input, and the `N − 1`
  encoding of `KeyCount`.

**Header + non-value overhead** = `2 + N·2 + (N − 1)·3 + 1 = 5N` bytes.
Total HSST size = `5N + ∑|Value_i|`.

**Lookup procedure**: identical to `TwoByteSlotValue` (read tail
`IndexType` → `0x06`; read `KeyCount` u16 LE at byte 0; binary-search
the `2·N`-byte key array at `[2, 2 + 2·N)`; resolve value bounds via
two `u24` LE reads — or zero for the omitted `Offset_0` and the
derived `Offset_N`).

**Restrictions and trade-offs.**

- All keys are exactly 2 bytes.
- Cumulative values are capped at `(1 << 24) − 1 = 16,777,215` bytes.
- `N ≤ 65,536`.
- One byte wider per offset than `TwoByteSlotValue`; pays back as soon
  as any single group exceeds 64 KiB (which would otherwise spill into
  a much heavier `BTree`).

## B-tree index node layout

Each node (root, intermediate, or leaf) is forward-readable from its start
offset (the leaf-pointer / child-pointer in the parent names that offset
directly; the root is located via
`root_start = HSST_end − 5 − RootPrefixLen − RootSize`).
The fixed-width metadata header sits at the front of the node so a single
read pulls in the header plus the keys/values prefix in cache; readers
parse forward into the keys section, then the values section.

```
[Metadata][Keys section][Values section]
^
node start
```

### Metadata

```
[Flags: u8][KeyCount: u16 LE][KeySize: u16 LE][CommonPrefixLen: u8][BaseOffset: 6 bytes LE]
```

The header is a fixed **12 bytes**. All fields are fixed-width — no varint
decoding on parse. With the 64 KiB node-size cap, every count/size field
fits in `u16`. `CommonKeyPrefix` bytes themselves are **not stored in the
node header** — see the "Common key prefix" paragraph below for how they
arrive.

`BaseOffset` is a **mandatory** fixed 6-byte little-endian unsigned integer
(low 48 bits; enough for any HSST up to 256 TiB). It sits at the tail of
the header so the fields needed to parse the keys section (`KeyCount`,
`KeySize`, `KeyType` and `IsKeyLittleEndian` from `Flags`, `CommonPrefixLen`)
group into the first 6 bytes; the cold-cache parse of the key-section
layout completes before paying for the `BaseOffset` read, which is only
consumed by value resolution after a successful floor match. The 6 bytes
are paid once per node, and per-entry value slot widths are picked from
`{2, 3, 4, 6}` to keep the total cheaper than always-4-byte slots. There
is no flag bit gating `BaseOffset`.

`Flags` bits — shared with the data-region's **per-entry leading flag
byte**, so the BTree reader's dispatch loop reads a single byte at the
current cursor and switches on `NodeKind` to decide whether it's sitting
on an entry, a leaf, or an intermediate. For entry-kind flag bytes, bits
2-7 are reserved and written as zero.

| Bit  | Meaning |
|------|---------|
| 0-1  | `NodeKind` — `00` = Entry (data-region entry), `01` = Leaf (BSearchIndex leaf node), `10` = Intermediate (BSearchIndex inner node), `11` reserved |
| 2-3  | `KeyType` — 0 Variable / 1 Uniform (value 2 reserved/unused) — leaf and intermediate only |
| 4-5  | `ValueSizeCode` — packs the per-entry value-slot width into 2 bits: `00`→2, `01`→3, `10`→4, `11`→6 — leaf and intermediate only |
| 6    | `IsKeyLittleEndian` — 1 = fixed-width key slots are stored byte-reversed so a native LE integer load matches lex order; set unconditionally for Variable (prefixArr is 2 bytes/slot) and for Uniform with `KeySize ∈ {2,4,8}` — leaf and intermediate only |
| 7    | Reserved — must be 0 |

**Common key prefix.** When `CommonPrefixLen > 0`, every stored key in the
node equals `CommonKeyPrefix || suffix_i` where `suffix_i` is what the
keys section encodes. The prefix bytes themselves are **not stored in the
node header** — they arrive from outside:

- For non-root nodes, from the parent's separator for this child. The
  parent's leaf/intermediate descender hands the matched separator (a
  full lex-order key constructed from the parent's `CommonKeyPrefix` plus
  the parent's stored suffix slot) to the child's parse routine.
- For the root, from the HSST trailer's `RootPrefix` bytes (the root has
  no parent to inherit from).

**`CommonPrefixLen` is uniform across every node in the HSST.** Every
leaf and every intermediate writes the same `CommonPrefixLen = G`, where
`G` is the `commonKeyPrefixLength` the caller passed to
`HsstBTreeBuilder` at construction (default `0`). The trailer's
`RootPrefix` carries those `G` bytes once for the whole HSST. Because the
parent's separator always starts with the parent's own `CommonKeyPrefix`
— which equals every other node's prefix — the first `G` bytes of any
parent separator are automatically the child's prefix; no per-level
"extend separator to at least the child's prefix" handshake is required.
Callers with random/hash-derived keys pass `0`; callers whose entries
share a structural prefix (e.g. an inner HSST under a fixed outer-key
prefix) pass the known length so leaves and intermediates can strip
those bytes off every stored slot.

`KeySize` / slot semantics apply to the *suffixes*. The builder caps `G`
at `min(keyLength, 128)` (the latter being the u8 header field's max).

`KeySize` semantics depend on `KeyType`:

- **Variable (0)** — the value of `KeySize` is the *Keys section's* total
  byte size. The section uses an SoA layout described in
  "Keys section (Variable)" below; its 14-bit tailOffset caps the
  section at 16 KiB.
- **Uniform (1)** — packed fixed-width entries. Each entry is exactly
  `KeySize` bytes; section size is `KeyCount * KeySize`.

`KeyType` value `2` is reserved/unused — it once selected a
`UniformWithLen` layout (fixed slot with a trailing length byte), now
removed. Readers fail with `InvalidDataException` if they encounter it.

**Value slot width.** Per-entry value slots are one of `{2, 3, 4, 6}`
bytes, encoded as the 2-bit `ValueSizeCode` field at `Flags` bits 4–5
(`00`→2, `01`→3, `10`→4, `11`→6). Values are always Uniform; there is no
Variable-value encoding for B-tree index nodes. The Values section is
`KeyCount * ValueSize` bytes. Widths outside `{2, 3, 4, 6}` are not
encodable — writers reject them and the natural-width rounding helper
rounds 0/1/2 → 2, 3 → 3, 4 → 4, and 5/6 → 6.

`BaseOffset` is added to every integer value read out of the node. The
writer picks `BaseOffset = min(values)` (when there's more than one
distinct value and the minimum is non-zero) and then stores each value
as a **Uniform unsigned LE integer** whose width is the smallest member
of `{2, 3, 4, 6}` that fits `max(values) − BaseOffset`. The chosen width
is recorded in the `ValueSizeCode` field, so a leaf with deltas that all
fit in 2 bytes stores 2-byte slots, while a leaf spanning a 5 GiB range
stores 6-byte slots.

### Children pointers (intermediate nodes)

For an intermediate node, each value is a `{2, 3, 4, 6}` byte
little-endian unsigned integer (Uniform; the byte width comes from
`ValueSizeCode`) interpreted (after `+ BaseOffset`) as the **inclusive
last byte** of the referenced child node within the HSST buffer
(0-indexed from the first byte of the HSST). The child's exclusive end =
`childOffset + 1`; the reader then loads the child from the end the same
way it loaded the root.

### Metadata-start pointers (leaves)

For a leaf node, each value is a `{2, 3, 4, 6}` byte little-endian
unsigned integer (after `+ BaseOffset`) giving the entry's `MetadataStart`
(for `BTree`, `0x01`) or `EntryStart` (for `BTreeKeyFirst`, `0x07`),
*relative to the start of the data region* (i.e. byte 0 of the HSST is
the first byte of the data region).

### Keys section (Variable)

When `KeyType = 0` (Variable), the Keys section uses a Structure-of-Arrays
layout that inlines the first two bytes of every key for cache-friendly
binary search:

```
[prefixArr: N·u16 LE][offsetArr: N·u16 LE][remainingkeys: tail bytes]
```

- **`prefixArr[i]`** holds the first 2 bytes of stored suffix `i`, with
  the two bytes byte-reversed on disk so that a u16 LE load of the slot
  yields a value whose unsigned numeric order matches the lex order of
  the original 2-byte prefix. Suffixes shorter than 2 bytes pad the slot
  with `0x00`; the length tag in `offsetArr` disambiguates.
- **`offsetArr[i]`** is a u16 LE packing `(lenTag << 14) | tailOffset`:
  `lenTag = 0b00` → suffix length 0; `0b01` → length 1; `0b10` → length
  2 (no tail bytes); `0b11` → length ≥ 3 with tail bytes at
  `remainingkeys[tailOffset ..]`. For tags `00`/`01`/`10` the cursor
  does not advance, so each such slot's `tailOffset` equals the next
  `0b11` entry's offset.
- **Tail length** (only meaningful for tag `0b11`) is sentinel-derived:
  `tail_i.length = offsetArr[i+1].tailOffset − offsetArr[i].tailOffset`,
  with the implicit sentinel for `i = N` being `remainingkeys.Length`.
- The 14-bit `tailOffset` field caps `remainingkeys` at **16 KiB**, which
  (combined with the 64 KiB per-node cap) bounds the entire Variable
  Keys section.

In this mode, the metadata's `KeySize` field carries the **total Variable
Keys section byte size** (= `4·N + tailBytes`), not a per-entry width.

## Constraints

- Maximum entries per leaf node: **64** by default; configurable at write
  time. Beyond that, the writer splits the leaf and promotes a separator
  into an intermediate node.
- Maximum key length per entry: **255 bytes**. Every entry in a BTree HSST
  shares the same key length, recorded once in the trailer as a single `u8`
  (so 0–255). Writers must reject longer keys and reject mid-build key-length
  changes.
- `MetadataLength` applies only to the `PackedArray` variant (`0x02`),
  whose metadata is a fixed 10-byte struct preceded by a single
  `MetadataLength: u8 = 10` byte. The `BTree` / `BTreeKeyFirst` variants
  have no `MetadataLength` field — their trailer is
  `[RootPrefix][RootPrefixLen][RootSize][KeyLength][IndexType]`.
- Per-entry value slots in B-tree index nodes are one of `{2, 3, 4, 6}`
  byte LE unsigned integers (width per the 2-bit `ValueSizeCode` in
  `Flags`). Combined with the mandatory 6-byte `BaseOffset`, a single
  HSST can address up to 256 TiB. The variable-section internal offset
  table (Variable key section) remains a `u16` per entry, so a single
  Variable section is still capped at 64 KiB. There is no in-format cap
  on a containing host file holding many HSSTs.

## Affected files

When changing this format, every file below has byte-level knowledge of
the layout and must be reviewed in lockstep with this document. If you
add a new file that encodes or decodes HSST bytes, append it here.

Writers / encoders:
- `Hsst/HsstBTreeBuilder.cs` — top-level HSST builder; writes the data region,
  drives the index builder, appends the trailing `IndexType` byte. Supports
  both `BTree` (0x01, key-after-value entries) and `BTreeKeyFirst` (0x07,
  key-first entries) via a constructor flag.
- `Hsst/HsstIndexBuilder.cs` — drives B-tree shape (leaf splitting,
  intermediate-node promotion). Aware of key-first entry layout so its
  separator-recompute reads can locate keys without skipping a LEB128.
- `BSearchIndex/BSearchIndexWriter.cs` — writes a single B-tree index
  node's bytes (`Metadata | Keys section | Values section`, with the
  fixed 12-byte metadata header at the front).
- `BSearchIndex/BSearchIndexLayoutPlanner.cs` — picks key/value section
  encodings (Variable / Uniform) and section sizes.
- `Hsst/IndexType.cs` — enum of valid index-type byte values.
- `Hsst/HsstPackedArrayBuilder.cs` / `Hsst/HsstPackedArrayReader.cs` — `PackedArray`
  writer / reader (recursive summary index; fixed 10-byte metadata).
- `Hsst/HsstDenseByteIndexBuilder.cs` — `DenseByteIndex` writer
  (descending-tag value layout; variable-width `Ends` table;
  `[Count][OffsetSize][IndexType]` trailer; tag-byte = array index).
- `Hsst/HsstTwoByteSlotValueBuilder.cs` — `TwoByteSlotValue` writer (fixed
  2-byte keys, variable values, u16 start-offset trailer).
- `Hsst/HsstTwoByteSlotValueLargeBuilder.cs` — `TwoByteSlotValueLarge`
  writer (same shape as `TwoByteSlotValue` but u24 offsets, ~16 MiB cap).

Readers / decoders:
- `Hsst/HsstReader.cs` — point-query reader; reads the trailing
  `IndexType` byte and walks the B-tree from the tail.
- `BSearchIndex/BSearchIndexReader.cs` — parses a single B-tree index
  node forward from its start offset; owns the on-disk header decode and
  the floor-search dispatch.
- `Hsst/HsstIndex.cs` — thin public wrapper over `BSearchIndexReader`
  preserving the `HsstIndex` API surface for callers.
- `Hsst/HsstDenseByteIndexReader.cs` — `DenseByteIndex` lookup helper
  (direct `Ends[k]` index, no tag scan); dispatched into from
  `HsstReader`.
- `Hsst/HsstPackedArrayReader.cs` — `PackedArray` lookup helper
  (recursive summary descent over fixed 10-byte metadata).
- `Hsst/HsstTwoByteSlotValueReader.cs` — `TwoByteSlotValue` lookup helper
  (binary search over the 2-byte key array; u16 LE offset resolution).
- `Hsst/HsstTwoByteSlotValueLargeReader.cs` — `TwoByteSlotValueLarge`
  lookup helper (same shape as `TwoByteSlotValueReader` but u24 LE reads).

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
- `Nethermind.State.Flat.Test/Hsst/HsstBTreeKeyFirstTests.cs` —
  `IndexType_Byte_Is_BTreeKeyFirst_At_Tail` and round-trip tests for the
  key-first variant (`0x07`).
- `Nethermind.State.Flat.Test/Hsst/HsstDenseByteIndexTests.cs` — trailer
  layout (including `OffsetSize` selection) and descending-tag value
  layout invariants.
- `Nethermind.State.Flat.Test/Hsst/HsstPackedArrayTests.cs` —
  fixed-metadata shape and summary-level math.
- `Nethermind.State.Flat.Test/Hsst/HsstCrossFormatTests.cs` —
  cross-variant invariants over the trailing `IndexType` dispatch.
- `Nethermind.State.Flat.Test/BSearchIndex/BSearchIndexTests.cs` — hex
  fixture tests for individual index nodes; `ReadFromStart(data, …)`
  call sites are sensitive to header byte positions.
