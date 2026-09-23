// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;

namespace Nethermind.Pbt;

internal static partial class TrieUpdater<TKey, TPath>
    where TKey : struct, IPbtKey<TKey>
    where TPath : struct, IPbtNodePath<TPath>
{
    /// <summary>The boundary slots still borrowing their value from the group being read.</summary>
    /// <remarks>
    /// Entries are filled by <see cref="Decompose"/> as it consumes the existing node group and taken back by
    /// <see cref="Compose"/> as it rebuilds, so a node here is always read against the traversal cursor itself.
    /// A node anchored at another group belongs in <see cref="BoundaryResults"/> instead.
    /// </remarks>
    internal struct Frontier
    {
        internal EntryBuffer Entries;
        internal uint Mask;

        internal TraversalSubtree Take(scoped ref GroupFrameReader<TKey, TPath> reader, PbtNodeGroupWriter<TPath> writer, PbtTraversalPath path, int slot) =>
            new(path, Entries[slot].TakeSubtree(ref reader, writer, path));

        internal void Set(int slot, ref TraversalSubtree result) => Entries[slot] = new(ref result.Node);
    }

    [InlineArray(PbtFourLevelGroupGeometry.BoundarySlots)]
    internal struct EntryBuffer { private DecompositionEntry _element; }
}
