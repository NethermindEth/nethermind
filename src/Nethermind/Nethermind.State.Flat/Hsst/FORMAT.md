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
| **Normal** | `[Version: u8 = 0x01][Data Region][Index Region]` |
| **Inline** | `[Version: u8 = 0x81][Index Region]` |

The high bit of the version byte selects the variant. The root B-tree node
lives at the *end* of the buffer and is read backward via the trailing
`MetadataLength` byte; there is no header trailer.

### Normal variant

The data region is a packed sequence of variable-length, **self-describing**
entries laid out value-first so that decoding is forward-readable from a
known `MetadataStart` cursor:

```
[Value: V bytes][ValueLength: LEB128][KeyLength: u8][FullKey: K bytes]
                ^
                MetadataStart  (= the index pointer's target byte)
```

`MetadataStart` is the byte offset (within the HSST buffer, *after* the
version byte) of the `ValueLength` LEB128. The leaf B-tree node stores this
offset for every entry; readers seek into the leaf, take the metaStart
pointer, then:

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

### Inline variant

There is no data region. Leaf B-tree nodes hold the values directly inside
the keys section's value slots. Separators in inline-mode leaves **are** the
full keys (no key reconstruction). Used for small fixed-width values where
the index-vs-data split would waste space — e.g. storage slot suffixes.

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
the version byte). The child's exclusive end = `childOffset + 1`; the
reader then loads the child from the end the same way it loaded the root.

### Metadata-start pointers (non-inline leaves)

For a non-inline leaf node, each value is a 4-byte little-endian `int`
(after `+ BaseOffset`) giving the entry's `MetadataStart`, *relative to the
start of the data region* (i.e. the offset within the HSST data region,
with index 0 being the byte right after the version byte).

### Inline values (inline leaves)

For inline-mode leaves, each value-section slot holds the full value bytes
directly — there's no metaStart indirection.

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
