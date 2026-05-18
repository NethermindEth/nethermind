// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State.Flat.BSearchIndex;

/// <summary>
/// What kind of addressable thing the reader is sitting on. Encoded in the low 2 bits of
/// every addressable thing's leading <c>Flags</c> byte so the BTree reader can dispatch
/// uniformly: read the flag byte at the current cursor, switch on <see cref="BSearchNodeKind"/>,
/// either decode an entry or descend into a child node.
/// </summary>
/// <remarks>
/// Values are fixed by the on-disk format — do not renumber.
/// </remarks>
public enum BSearchNodeKind : byte
{
    /// <summary>
    /// Data-region entry. The flag byte sits at the entry's <c>MetadataStart</c> (key-after-value)
    /// or <c>EntryStart</c> (key-first); the remaining entry layout follows immediately after.
    /// Bits 2–7 of the flag byte are reserved and written as zero for entries.
    /// </summary>
    Entry = 0,
    /// <summary>Bottom-of-tree <see cref="BSearchIndex"/> node whose value slots point at entries.</summary>
    Leaf = 1,
    /// <summary>Inner <see cref="BSearchIndex"/> node whose value slots point at other nodes or at entries.</summary>
    Intermediate = 2,
    // Value 3 is reserved.
}
