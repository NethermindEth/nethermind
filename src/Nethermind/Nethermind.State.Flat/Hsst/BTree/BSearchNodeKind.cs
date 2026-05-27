// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State.Flat.Hsst.BTree;

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
    /// <summary>
    /// A <see cref="BSearchIndex"/> node. Value slots point at children — entries (page-local
    /// leaf level), other Intermediate nodes (inner levels), or a mix. There is no separate
    /// "leaf" on-disk kind: a node whose value slots all point at entries is conceptually a
    /// leaf but encodes the same way. Consumers that need the "leaf level" semantics peek the
    /// leftmost child's flag byte (see <c>HsstEnumerator.DescendToLeaf</c>).
    /// </summary>
    Intermediate = 1,
    // Values 2 and 3 are reserved.
}
