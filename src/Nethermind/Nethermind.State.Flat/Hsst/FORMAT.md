# HSST — Hierarchical Static Sorted Table

A compact, immutable binary format for sorted key/value tables. Used as the
on-disk column layout for persisted snapshots.

## Top-level layout

| Variant | Bytes |
|---|---|
| **Normal** | `[Version: u8 = 0x01][Data Region][Index Region]` |
| **Inline** | `[Version: u8 = 0x81][Index Region]` |

The high bit of the version byte selects the variant. The root B-tree node lives
at the *end* of the buffer and is read backward via the trailing
`MetadataLength` byte; there is no header trailer.

### Normal variant

The data region is a packed sequence of variable-length entries, each laid out
**value-first** so that decoding is forward-readable from a known
`MetadataStart` cursor:

```
[Value: V bytes][ValueLength: LEB128][RemainingKeyLength: LEB128][RemainingKey: K bytes]
                ^
                MetadataStart
```

`MetadataStart` is the byte offset (within the HSST buffer, *after* the version
byte) of the `ValueLength` LEB128. The leaf B-tree node stores this offset for
every entry; readers seek into the leaf, take the metaStart pointer, then:

1. Decode `ValueLength` (LEB128) — the value bytes live at
   `[metaStart - ValueLength, metaStart)`.
2. Decode `RemainingKeyLength` (LEB128).
3. Read `RemainingKey` bytes — combined with the leaf's stored *separator*
   they form the full stored key (`fullKey = separator + remainingKey`).
   The B-tree uses minimum separators, so the separator is typically a prefix
   of the full key with `RemainingKey` filling in the suffix.

### Inline variant

There is no data region. Leaf B-tree nodes hold the values directly inside the
keys section's value slots. Separators in inline-mode leaves **are** the full
keys (no `RemainingKey` concatenation step). Used for small fixed-width values
where the index-vs-data split would waste space — e.g. storage slot suffixes.

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
[Flags: u8][KeyCount: LEB128][KeySize: LEB128][ValueSize: LEB128][BaseOffset: LEB128 optional]
```

`Flags` bits:

| Bit  | Meaning |
|------|---------|
| 0    | `IsIntermediate` — 1 = intermediate B-tree node, 0 = leaf |
| 1–2  | `KeyType`        — 0 Variable / 1 Uniform / 2 UniformWithLen |
| 3–4  | `ValueType`      — 0 Variable / 1 Uniform / 2 UniformWithLen |
| 5    | `HasBaseOffset`  — 1 = `BaseOffset` LEB128 follows |
| 6    | reserved (0)    |
| 7    | reserved (0)    |

`KeySize` / `ValueSize` semantics depend on the corresponding type:

- **Variable (0)** — the value of `KeySize`/`ValueSize` is the *section's*
  total byte size. The section starts with a `KeyCount * 2`-byte little-endian
  offset table, followed by `LEB128 length || bytes` per entry at the indexed
  offset.
- **Uniform (1)** — packed fixed-width entries. Each entry is exactly
  `KeySize` (or `ValueSize`) bytes; section size is `KeyCount * size`.
- **UniformWithLen (2)** — fixed slot size, but the last byte of each slot
  records the actual byte length used. Section size still `KeyCount * size`.

`BaseOffset`, when present, is added to every integer value read out of the
node. This is the trick that lets intermediate nodes and leaves with
metaStart-pointers store offsets in 4 bytes even when the underlying buffer is
larger than `int.MaxValue`-encodable: pick a base near the cluster of values
and store small deltas off it.

### Children pointers (intermediate nodes)

For an intermediate node, each value is a 4-byte little-endian `int` (Uniform,
4) interpreted (after `+ BaseOffset`) as the **inclusive last byte** of the
referenced child node within the HSST buffer (0-indexed from the version byte).
The child's exclusive end = `childOffset + 1`; the reader then loads the child
from the end the same way it loaded the root.

### Metadata-start pointers (non-inline leaves)

For a non-inline leaf node, each value is a 4-byte little-endian `int` (after
`+ BaseOffset`) giving the entry's `MetadataStart`, *relative to the start of
the data region* (i.e. the offset within the HSST data region, with index 0
being the byte right after the version byte).

### Inline values (inline leaves)

For inline-mode leaves, each value section slot holds the full value bytes
directly — there's no metaStart indirection.

## Constraints

- `MaxLeafEntries = 64` (configurable per `HsstBuilder.Build`). Beyond this, the
  builder splits the leaf and promotes a separator into an intermediate node.
- `MetadataLength` is a single byte → metadata section ≤ 255 bytes.
- All offsets within a node are encoded as `int` (4 bytes); a single HSST is
  thus capped at ~2 GiB. The reader interface (`IHsstByteReader<TPin>`) uses
  `long` for outer offsets so a host file can be larger than 2 GiB even though
  each contained HSST is not.

## Reader/writer types

| Role | Type | Notes |
|---|---|---|
| Build | `HsstBuilder<TWriter>` | Generic over `IByteBufferWriter`. `MaxLeafEntries` constant lives here. |
| Random-access read | `HsstReader<TReader, TPin>` | Generic over `IHsstByteReader<TPin>`. `TrySeek` is exact-match; `TrySeekFloor` for largest-entry-≤-key. |
| Forward iteration | `HsstEnumerator<TReader, TPin>` | Stack-based B-tree walker; one pin held at a time, ancestors re-loaded on ascend. |
| N-way sort-merge | `HsstMergeEnumerator` | Class-based offset-table cursor (heap-allocated; multiple instances live in arrays for compaction). |

`SpanByteReader` + `NoOpPin` is the standard in-memory backing — zero-copy
`PinBuffer` returns a slice of the underlying `ReadOnlySpan<byte>`.
`PooledArrayPin` is the canonical copy-fallback for paged/stream readers that
can't produce a contiguous span on demand.

## Caller-visible API

- `HsstReader.TrySeek(key, out previousBound)` — exact match. Sets the reader's
  `Bound` to the matched value's region, outs the prior bound for restoration.
- `HsstReader.TrySeekFloor(key, out previousBound)` — floor (largest stored key
  ≤ `key`). Used for prefix/range scans and for cases where the caller wants
  best-effort traversal without a hard exact-match requirement.
- `HsstReader.GetValue(output)` / `GetBound()` — extract the value at the
  current bound, either by copying into a span or by returning the absolute
  `(offset, length)` tuple.
- `HsstEnumerator.MoveNext()` / `Current` — yields `(Key span, ValueBound)`
  pairs in sorted order. `Key` lives in the enumerator's inline buffer and is
  invalidated on the next `MoveNext`; `ValueBound` is an absolute
  `(reader-offset, length)` tuple stable for the reader's lifetime.

## Where to look in code

- `Hsst/HsstBuilder.cs` — write path, format invariants, `MaxLeafEntries`
- `Hsst/HsstReader.cs` — exact + floor seek, B-tree descent
- `Hsst/HsstEnumerator.cs` — stack-based forward iteration
- `Hsst/HsstMergeEnumerator.cs` — N-way merge cursor
- `Hsst/IHsstByteReader.cs` — reader/pin abstraction (`TryRead`,
  `TryReadWithReadahead`, `PinBuffer`)
- `BSearchIndex/BSearchIndexReader.cs` — node-level binary search +
  metadata layout (the format spec for one B-tree node)
- `BSearchIndex/BSearchIndexWriter.cs` — node-level write path
