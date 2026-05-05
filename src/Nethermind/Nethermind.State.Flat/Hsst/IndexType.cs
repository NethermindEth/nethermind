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
    BTreeInlineValue = 0x02,
    BTreeHashIndex = 0x03,
    /// <summary>
    /// Fixed-size key/value layout. Replaces the b-tree with a packed entry array, a sparse
    /// "checkpoint" binary index (every ~1 KiB by default) for two-level binary search, and an
    /// always-present open-addressed hash index. Requires every key and every value to be the
    /// same size.
    /// </summary>
    PackedArray = 0x06,
    /// <summary>
    /// Tiny single-byte-keyed map (≤ 32 entries). Replaces the b-tree with a flat
    /// trailer of `[Ends: N×u32 LE][Tags: N×u8][Count: u8][IndexType: u8]` over a
    /// concatenated value region. Lookup is a linear/SIMD scan of the tag bytes
    /// followed by an index into `Ends` — no LEB128 / b-tree machinery.
    /// </summary>
    ByteTagMap = 0x08,
}
