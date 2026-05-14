// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State.Flat.Hsst;

/// <summary>
/// Discriminator written as the last byte of an HSST. Selects which index strategy
/// the rest of the blob uses. New strategies get a new value; this is not a bitfield.
/// </summary>
public enum IndexType : byte
{
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
    /// Fixed 2-byte key, variable value, packed start-offset trailer. Concatenated
    /// values followed by <c>[Offset_1..Offset_{N-1}: u16 LE][Key_0..Key_{N-1}: 2 bytes each][KeyCount: u16 LE = N − 1][IndexType: u8]</c>.
    /// <c>Offset_0</c> is omitted (always 0); <c>Offset_N</c> is derived from the
    /// trailer length. Data region is capped at 65,535 bytes by the u16 offset width.
    /// See FORMAT.md for full layout / lookup procedure.
    /// </summary>
    TwoByteSlotValue = 0x05,
    /// <summary>
    /// Wider sibling of <see cref="TwoByteSlotValue"/>: same layout but u24 LE offsets,
    /// raising the data-region cap from 64 KiB to ~16 MiB. Trailer is
    /// <c>[Offset_1..Offset_{N-1}: u24 LE][Key_0..Key_{N-1}: 2 bytes each][KeyCount: u16 LE = N − 1][IndexType: u8]</c>.
    /// Picked when the cumulative SlotSuffix payload exceeds the u16 sibling's cap.
    /// See FORMAT.md for full layout / lookup procedure.
    /// </summary>
    TwoByteSlotValueLarge = 0x06,
}
