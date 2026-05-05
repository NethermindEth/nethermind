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
    FlatEntries = 0x06,
    /// <summary>
    /// Same as <see cref="FlatEntries"/> but with the binary index laid out as two parallel
    /// arrays: all checkpoint keys contiguous, followed by all checkpoint entry indices
    /// contiguous. Built for comparison against the interleaved layout — checkpoint-key
    /// binary search reads tighter, contiguous slabs of key bytes.
    /// </summary>
    FlatEntriesSplitIndex = 0x07,
}
