// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;

namespace Nethermind.Pbt;

internal static partial class TrieUpdater<TKey, TPath>
    where TKey : struct, IPbtKey<TKey>
    where TPath : struct, IPbtNodePath<TPath>
{
    /// <summary>The boundary slots of the group being rebuilt.</summary>
    /// <remarks>
    /// Entries are filled by <see cref="Decompose"/> as it consumes the existing node group, and by the folds that
    /// replace its touched slots, then taken back by <see cref="Compose"/> as it rebuilds. Every node here is read
    /// against the traversal cursor, so a node anchored elsewhere is re-anchored before it is placed.
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
