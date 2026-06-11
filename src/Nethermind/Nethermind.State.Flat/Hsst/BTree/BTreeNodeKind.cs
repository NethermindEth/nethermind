// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State.Flat.Hsst.BTree;

/// <summary>
/// What the reader is sitting on, encoded in the low 2 bits of the leading <c>Flags</c> byte
/// so the BTree reader can dispatch on it: decode an entry or descend into a node.
/// </summary>
/// <remarks>Values are fixed by the on-disk format — do not renumber.</remarks>
public enum BTreeNodeKind : byte
{
    /// <summary>A data-region entry: the full key and value.</summary>
    Entry = 0,
    /// <summary>
    /// A <see cref="BTreeNode"/> whose value slots point at children — entries, other nodes, or a
    /// mix. There is no separate on-disk "leaf" kind.
    /// </summary>
    Intermediate = 1,
    // Values 2 and 3 are reserved.
}
