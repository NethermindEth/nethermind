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
| **BTree** | `[Data Region][Index Region][IndexType: u8 = 0x01]` |
| **BTreeHashIndex** | `[Data Region][Index Region][HashTable: 4·N bytes][TableSize: u32 LE][IndexType: u8 = 0x03]` |
| **FlatEntries** | `[Data][Summary L0]…[Summary L(D-1)][HashTable: 4·TableSize bytes (omitted when 0)][Metadata][MetadataLength: u8][IndexType: u8 = 0x06]` |
| **ByteTagMap** | `[Value_0]…[Value_{N-1}][Ends: N·u32 LE][Tags: N·u8][Count: u8 = N][IndexType: u8 = 0x08]` |

The trailing **index type byte** is the last byte of the HSST and selects
the variant by enumerated value (not a bitfield):

| Value | Name | Meaning |
|---|---|---|
| `0x01` | `BTree` | Separate data region; leaves hold metaStart pointers. |
| `0x03` | `BTreeHashIndex` | `BTree` plus a trailing open-address hash table of metaStart pointers. |
| `0x06` | `FlatEntries` | Fixed-size key/value array with a recursive "summary" index and an optional hash table. |
| `0x08` | `ByteTagMap` | Tiny single-byte-keyed map (≤ 255 entries) — flat tag/end-offset trailer over a concatenated value region. |

Other values are reserved for future index strategies. The root B-tree
node lives just before the index type byte (or just before the hash table,
for `BTreeHashIndex`) and is read backward via its trailing `MetadataLength`
byte; there is no header.

### BTree variant

The data region is a packed sequence of variable-length, **self-describing**
entries laid out value-first so that decoding is forward-readable from a
known `MetadataStart` cursor:

```
[Value: V bytes][ValueLength: LEB128][KeyLength: u8][FullKey: K bytes]
                ^
                MetadataStart  (= the index pointer's target byte)
```

`MetadataStart` is the byte offset (within the HSST buffer, measured from
byte 0 — the first byte of the data region) of the `ValueLength` LEB128.
The leaf B-tree node stores this offset for every entry; readers seek into
the leaf, take the metaStart pointer, then:

1. Decode `ValueLength` (LEB128) — the value bytes live at
   `[MetadataStart - ValueLength, MetadataStart)`.
2. Read `KeyLength` (single `u8`, 0–255).
3. The full key sits at `[MetadataStart + lebBytes + 1, MetadataStart + lebBytes + 1 + KeyLength)`.

**Why `MetadataStart` aims at `ValueLength` and not at the value.** Values
are unbounded (KiB–MiB, including nested HSSTs) so `ValueLength` is LEB128.
LEB128 has a forward-only terminator (high-bit "continuation" chain): given
a byte mid-stream you can't tell whether you're inside someone else's
continuation run or sitting at the start of a fresh varint. So the format
places the length *after* the value and aims the index pointer at it; the
value is back-derived from `MetadataStart - ValueLength`. The fixed-width
`KeyLength` then `FullKey` are forward-decoded after that. This is a
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

### BTreeHashIndex variant

A `BTree` with an extra open-address hash table appended after the root.
Layout, reading backward from the index type byte:

```
... B-tree root ... [HashTable][TableSize: u32 LE = N][IndexType: u8 = 0x03]
```

- `TableSize` (`N`) is a 4-byte little-endian unsigned integer; the table
  holds exactly `N` slots. With Lemire's multiply-shift reduction `N` need
  not be a power of two.
- `HashTable` is `N` slots of `u32` little-endian, each one of:
  - `0x00000000` — **empty**: no entry hashes to this slot.
  - `0xFFFFFFFF` — **collision sentinel**: two or more entries hashed here;
    the reader must consult the B-tree.
  - any other value — a `MetadataStart` pointer with the same encoding as a
    B-tree leaf value (see "BTree variant"): byte offset relative
    to byte 0 of the HSST.

Slot index for a key:

```
slot = (uint)(((ulong)HashKey(key) * (ulong)N) >> 32)
```

Where `HashKey` is the low 32 bits of `XxHash3` over the full key bytes
(no prefix stripping); writer and reader must compute it identically.
This is Daniel Lemire's multiply-shift reduction — uniform on `[0, N)`
without requiring `N` to be a power of two
(<https://lemire.me/blog/2016/06/27/a-fast-alternative-to-the-modulo-reduction/>).

The empty sentinel is unambiguous because in a valid `BTreeHashIndex` HSST
the data region is non-empty (an empty HSST is encoded as plain `BTree`),
so a real `MetadataStart` is always nonzero. The collision sentinel
`0xFFFFFFFF` is unambiguous because `MetadataStart` for a single HSST
cannot reach `2^32 - 1` (the HSST is bounded by the surrounding 4-byte
B-tree pointer encoding, ≈2 GiB).

**Lookup procedure.** Compute `slot`. Read the slot value:

1. **Empty.** No entry could match; exact lookup returns "not found". A
   floor lookup must still consult the B-tree.
2. **Collision.** Multiple keys hashed to this slot; consult the B-tree.
3. **Pointer.** Resolve the candidate exactly as for a B-tree
   leaf hit: decode `ValueLength`/`KeyLength` at the `MetadataStart` cursor
   and compare the stored key to the input. On match, return; on mismatch
   (the candidate's hash collides with the input's hash), exact lookup
   returns "not found" and floor must consult the B-tree.

**Sizing.** Builders pick `N = max(1, ceil(entries / targetUtilization))`
(default target `0.75`); the target is a build-time knob, never recorded
in the file.

The B-tree under the hash table is identical to a `BTree` HSST and remains
authoritative — readers that only know `BTree` could parse this variant by
peeling off the trailing `5 + 4·N` bytes and reading the rest as a
`BTree` HSST. The hash table is purely a fast path.

### FlatEntries variant

A specialised layout for fixed-size keys and values. The b-tree is replaced
by a packed entry array with a recursive "summary" index and an optional
hash table.

```
[Data][Summary L0]…[Summary L(D-1)][HashTable: 4·TableSize bytes (omitted when 0)][Metadata][MetadataLength: u8][IndexType: u8 = 0x06]
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
  slot stores `entryIndex + 1` (1-based). Hash function is the same
  `HashKey` (low 32 bits of `XxHash3`) as `BTreeHashIndex`; the slot is
  derived via Lemire's multiply-shift reduction
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

### ByteTagMap variant

A specialised layout for tiny single-byte-keyed maps where the b-tree's fixed
parse cost (LEB128 metadata, separator/full-key duplication, leaf binary
search) dominates payload work. Targets the persisted-snapshot column
container (≤7 entries), per-address sub-tag map (≤3 entries), and the
slot-suffix bucket under a 31-byte slot prefix (≤256 distinct suffix bytes,
encoded up to the u8 `Count` cap of 255).

```
[Value_0][Value_1]…[Value_{N-1}][Ends: N·u32 LE][Tags: N·u8][Count: u8 = N][IndexType: u8 = 0x08]
```

Section ordering rationale: `Tags` is touched on every lookup (linear /
SIMD scan); `Ends` is only consulted *after* a tag hit. Placing `Tags`
adjacent to `[Count][IndexType]` keeps the lookup-critical bytes on the
same cache line as the trailer bytes the reader fetches first.

- **`Value_i`** — raw bytes of the value associated with the i-th tag
  (in ascending tag order). Values may themselves be nested HSSTs, exactly
  like `BTree`. There is no length prefix in front of each value; lengths
  are derived from `Ends` differences.
- **`Ends`** — `N` little-endian `u32`s. `Ends[i]` is the **exclusive end
  offset** of `Value_i` measured from byte 0 of the HSST. Equivalently,
  the start of `Value_{i+1}` (or the first byte of the `Ends` section
  itself when `i = N-1`). The start of `Value_i` is `i == 0 ? 0 : Ends[i-1]`,
  and its length is `Ends[i] - (i == 0 ? 0 : Ends[i-1])`. Because `Ends`
  values are absolute offsets within the HSST, a single `ByteTagMap` HSST
  is capped at ≈4 GiB — same effective limit as the b-tree variants.
- **`Tags`** — `N` bytes, strictly ascending. Used for lookup; uniqueness
  is a build-time invariant.
- **`Count`** — single byte, holds `N`. Capped at **255** (the u8 limit;
  `0` is reserved for the empty case). Beyond that, callers should use
  `BTree` instead. The empty case (`N = 0`) encodes as the 2-byte sequence
  `[0x00][0x08]`.

**Lookup procedure** (exact and floor):

1. Read tail byte → `IndexType` must equal `0x08`.
2. Read byte at `end - 2` → `N`. If `N == 0`, no entry → not found.
3. `Tags` lives at `[end - 2 - N, end - 2)` — directly adjacent to
   `Count`, no further offset math. `Ends` lives at
   `[end - 2 - N - 4·N, end - 2 - N)` and is only consulted after a hit.
4. Linear scan `Tags` for the requested byte (one `Vector128<byte>`
   compare-equal covers `N ≤ 16`; two for `N ≤ 32`). For floor, take the
   largest tag whose 1-byte key is `≤` the input's first byte (a
   multi-byte input compares strictly greater than the matching 1-byte
   tag, so the floor is still the largest tag `≤ input[0]`). Miss →
   not found (exact) or fall-through (floor with no candidate ≤).
5. Hit at index `i`: read `Ends[i]` (and `Ends[i-1]` if `i > 0`) to get
   `valueStart = i == 0 ? 0 : Ends[i-1]`, `valueEnd = Ends[i]`. Return
   the value span `[valueStart, valueEnd)`.

No LEB128, no b-tree node parse, no separator/full-key duplication. The
trailer cost is `5·N + 2` bytes regardless of value sizes.

**Restrictions and trade-offs.**

- All keys are exactly 1 byte. Multi-byte keys are rejected at build time.
- `N ≤ 32` (one-byte `Count`). Larger maps must use `BTree` /
  `BTreeHashIndex`.
- HSST size capped at ≈4 GiB (u32 `Ends`).
- Per-entry overhead is 5 bytes (1 tag + 4 end-offset); plus the
  2-byte trailer footer. No b-tree, no leaf metadata, no per-entry
  LEB128 length prefix in the data region.

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
[Flags: u8][KeyCount: LEB128][KeySize: LEB128][ValueSize: LEB128][BaseOffset: LEB128 optional][CommonKeyPrefixLen: u8 + bytes optional]
```

`Flags` bits:

| Bit  | Meaning |
|------|---------|
| 0    | `IsIntermediate` — 1 = intermediate B-tree node, 0 = leaf |
| 1–2  | `KeyType`        — 0 Variable / 1 Uniform / 2 UniformWithLen |
| 3–4  | `ValueType`      — 0 Variable / 1 Uniform / 2 UniformWithLen |
| 5    | `HasBaseOffset`  — 1 = `BaseOffset` LEB128 follows |
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
node. This lets intermediate nodes and leaves with metaStart-pointers store
offsets in 4 bytes even when the underlying buffer is larger than the
naive `int` range: pick a base near the cluster of values and store small
deltas off it.

### Children pointers (intermediate nodes)

For an intermediate node, each value is a 4-byte little-endian `int`
(Uniform, 4) interpreted (after `+ BaseOffset`) as the **inclusive last
byte** of the referenced child node within the HSST buffer (0-indexed from
the first byte of the HSST). The child's exclusive end = `childOffset + 1`;
the reader then loads the child from the end the same way it loaded the root.

### Metadata-start pointers (leaves)

For a leaf node, each value is a 4-byte little-endian `int` (after `+ BaseOffset`)
giving the entry's `MetadataStart`, *relative to the start of the data region*
(i.e. byte 0 of the HSST is the first byte of the data region).

## Constraints

- Maximum entries per leaf node: **64** by default; configurable at write
  time. Beyond that, the writer splits the leaf and promotes a separator
  into an intermediate node.
- Maximum key length per entry: **255 bytes**, encoded as a single `u8`.
  Writers must reject longer keys.
- `MetadataLength` is a single byte → metadata section ≤ 255 bytes.
- All offsets *within* a node are encoded as 4-byte little-endian
  integers, so a single HSST is capped at ≈2 GiB. There is no in-format
  cap on a containing host file holding many HSSTs.

## Affected files

When changing this format, every file below has byte-level knowledge of
the layout and must be reviewed in lockstep with this document. If you
add a new file that encodes or decodes HSST bytes, append it here.

Writers / encoders:
- `Hsst/HsstBuilder.cs` — top-level HSST builder; writes the data region,
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
- `Hsst/HsstFlatBuilder.cs` / `Hsst/HsstFlatReader.cs` — `FlatEntries`
  writer / reader (recursive summary index, optional hash table).
- `Hsst/HsstByteTagMapBuilder.cs` — `ByteTagMap` writer (concatenated
  values + flat tag/end-offset trailer).

Readers / decoders:
- `Hsst/HsstReader.cs` — point-query reader; reads the trailing
  `IndexType` byte and walks the B-tree from the tail.
- `Hsst/HsstIndex.cs` — parses a single index node from its tail.
- `BSearchIndex/BSearchIndexReader.cs` — alternate index-node decoder
  used by the merge path; mirrors `HsstIndex` parsing.
- `BSearchIndex/BSearchIndexReaderSimd.cs` — SIMD fast paths over
  fixed-width key/value sections; tied to the section encodings the
  layout planner can choose.
- `Hsst/HsstByteTagMapReader.cs` — `ByteTagMap` lookup helper (linear
  tag scan + Ends-derived value bound); dispatched into from
  `HsstReader`/`HsstEnumerator`/`HsstMergeEnumerator`.

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
