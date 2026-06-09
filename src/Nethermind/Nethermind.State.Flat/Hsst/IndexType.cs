// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State.Flat.Hsst;

/// <summary>
/// Discriminator written as the last byte of an HSST. Selects which index strategy
/// the rest of the blob uses. New strategies get a new value; this is not a bitfield.
/// </summary>
public enum IndexType : byte
{
    /// <summary>
    /// B-tree HSST with key-after-value data-region entries. Each entry is
    /// <c>[Value][ValueLength: LEB128][FullKey]</c>; the leaf index pointer targets the
    /// LEB128 byte (MetadataStart), and the reader recovers the value via
    /// <c>ValueStart = MetadataStart − ValueLength</c>. Best for non-slot levels where
    /// the streaming write API (BeginValueWrite / FinishValueWrite) is wanted.
    /// </summary>
    BTree = 0x01,
    /// <summary>
    /// Fixed-size key/value layout. Replaces the b-tree with a packed entry array, a sparse
    /// "checkpoint" binary index (every ~1 KiB by default) for two-level binary search, and an
    /// always-present open-addressed hash index. Requires every key and every value to be the
    /// same size.
    /// </summary>
    PackedArray = 0x02,
    // 0x03 is reserved (previously ByteTagMap). Do not reuse without a wire-format bump.
    /// <summary>
    /// Byte-addressed array map. The tag byte is the array index directly: lookup of
    /// single-byte key <c>k</c> resolves to <c>Ends[k]</c> with no tag scan. Trailer is
    /// <c>[Ends: N·u32 LE][Count: u8 = N − 1][IndexType: u8]</c> — no tags array.
    /// Entries that were not explicitly written are gap-filled with zero-length
    /// values (the cumulative end equals the previous entry's end). Used by the
    /// persisted-snapshot outer column container and the per-address sub-tag
    /// container, where the set of tag positions is fixed and known.
    /// </summary>
    DenseByteIndex = 0x04,
    /// <summary>
    /// Fixed 2-byte key, variable value, keys-first wire shape. Layout is
    /// <c>[KeyCount: u16 LE = N − 1][Key_0..Key_{N-1}: 2 bytes each][Offset_1..Offset_{N-1}: u16 LE][Value_0..Value_{N-1}][IndexType: u8]</c>.
    /// <c>Offset_0</c> is omitted (always 0); <c>Offset_N</c> is derived from the blob
    /// length minus the trailing <see cref="IndexType"/> byte. Cumulative values are
    /// capped at 65,535 bytes by the u16 offset width. See FORMAT.md for full layout /
    /// lookup procedure.
    /// </summary>
    TwoByteSlotValue = 0x05,
    /// <summary>
    /// Wider sibling of <see cref="TwoByteSlotValue"/>: same keys-first layout but u24 LE
    /// offsets, raising the values-section cap from 64 KiB to ~16 MiB.
    /// <c>[KeyCount: u16 LE = N − 1][Key_0..Key_{N-1}: 2 bytes each][Offset_1..Offset_{N-1}: u24 LE][Value_0..Value_{N-1}][IndexType: u8]</c>.
    /// Picked when the cumulative SlotSuffix payload exceeds the u16 sibling's cap.
    /// See FORMAT.md for full layout / lookup procedure.
    /// </summary>
    TwoByteSlotValueLarge = 0x06,
    /// <summary>
    /// B-tree HSST with key-first data-region entries. Each entry is
    /// <c>[FullKey][ValueLength: LEB128][Value]</c>; the leaf index pointer targets the
    /// FullKey byte 0 (EntryStart), and the reader walks forward (key length comes from
    /// the trailer, LEB128 is forward-readable). Selected by callers whose values are
    /// large nested HSSTs (e.g. slot-level B-trees over sub-slot HSSTs) so the outer
    /// entry's per-entry metadata sits at the entry's *front*, parallel to the inner
    /// HSST's keys-first layout. Streaming writes are not supported in this mode — the
    /// builder requires <c>Add(key, valueSpan)</c>.
    /// </summary>
    BTreeKeyFirst = 0x07,
    // 0x08–0x0B reserved (were the partitioned variants — partitioning and the per-partition
    // hashtable are now folded into 0x01 / 0x07 via the BTreeNodeKind.Hashtable node, so a
    // partitioned blob is an ordinary BTree / BTreeKeyFirst whose directory leaf children are
    // Hashtable nodes; no distinct index type). See FORMAT.md and BTreeNodeKind.
}
