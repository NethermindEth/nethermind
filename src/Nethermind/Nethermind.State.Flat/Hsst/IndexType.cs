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
    /// <summary>
    /// Tiny single-byte-keyed map (≤ 32 entries). Replaces the b-tree with a flat
    /// trailer of `[Ends: N×u32 LE][Tags: N×u8][Count: u8][IndexType: u8]` over a
    /// concatenated value region. Lookup is a linear/SIMD scan of the tag bytes
    /// followed by an index into `Ends` — no LEB128 / b-tree machinery.
    /// </summary>
    ByteTagMap = 0x03,
    /// <summary>
    /// Byte-addressed array map. Like <see cref="ByteTagMap"/> but the tag byte is
    /// the array index directly: lookup of single-byte key <c>k</c> resolves to
    /// <c>Ends[k]</c> with no tag scan. Trailer is
    /// <c>[Ends: N·u32 LE][Count: u8 = N − 1][IndexType: u8]</c> — no tags array.
    /// Entries that were not explicitly written are gap-filled with zero-length
    /// values (the cumulative end equals the previous entry's end). Used by the
    /// persisted-snapshot outer column container and the per-address sub-tag
    /// container, where the set of tag positions is fixed and known.
    /// </summary>
    DenseByteIndex = 0x04,
    /// <summary>
    /// Variable-size-value packed array. Like <see cref="PackedArray"/> but values
    /// are variable-length and stored packed up front. The key+offset section after
    /// the values keeps a fixed stride <c>KeySize + OffsetSize</c> so binary search
    /// and recursive summary descent work unchanged. Each entry stores
    /// <c>[Key: KeySize][EndOffset: OffsetSize, LE]</c>; value_i lives in
    /// <c>Values[EndOffset_{i-1} .. EndOffset_i)</c> with <c>EndOffset_{-1} := 0</c>.
    /// <c>OffsetSize</c> is chosen at build time to fit <c>ValuesTotalLength</c>
    /// (1, 2, 4, or 6 bytes — 6-byte LE covers up to 256 TiB).
    /// Build-time cost: keys and per-entry end offsets are buffered in memory
    /// until finalize (the key+offset table is emitted AFTER values, and
    /// <c>OffsetSize</c> can't be picked until the total values length is known).
    /// Values themselves stream straight to the writer — no value buffering.
    /// </summary>
    VarPackedArray = 0x05,
}
