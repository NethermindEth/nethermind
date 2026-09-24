// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Nethermind.Pbt;

internal static partial class TrieUpdater<TKey, TPath>
    where TKey : struct, IPbtKey<TKey>
    where TPath : struct, IPbtNodePath<TPath>
{
    /// <summary>The boundary slots of the group being rebuilt.</summary>
    /// <remarks>
    /// Entries are filled by <see cref="Decompose"/> as it resolves the existing node group, and by the folds that
    /// replace its touched slots, then taken back by <see cref="Compose"/> as it rebuilds. A decomposed entry names
    /// where its node is read from rather than holding it, so only the folds' own results are carried here. A stored
    /// node no touched slot lies under has no entry, since composition copies it without consulting the frontier.
    /// <see cref="Mask"/> marks the positions that hold a node. Taking one leaves its bit set: <see cref="SetBoundary"/>
    /// rewrites a folded slot's bit, and composition visits each position once.
    /// </remarks>
    internal struct Frontier
    {
        internal EntryBuffer Entries;
        internal uint Mask;
        /// <summary>The stored positions with no touched slot under them, which <see cref="Compose"/> copies straight from the frame.</summary>
        internal uint Copies;
        /// <summary>The input node, for the slots that resolve to the group's own root instead of a node inside it.</summary>
        internal BoundaryNode Root;
        /// <summary>The folds' results by slot, rented on the first one so a frame does not carry a node per slot.</summary>
        private FoldResult[]? _results;

        /// <summary>Records that <paramref name="slot"/> is read from <paramref name="source"/>, occupying <paramref name="position"/>.</summary>
        internal void Place(int slot, int position, EntrySource source, int sourcePosition)
        {
            Entries[slot] = new DecompositionEntry(source, sourcePosition);
            Mask |= 1u << position;
        }

        /// <summary>Takes the boundary node at <paramref name="slot"/>, resolving it against the frame it was read from.</summary>
        internal BoundaryNode TakeBoundaryNode(scoped ref GroupFrameReader<TKey, TPath> reader, scoped in PbtTraversalPath path, int slot)
        {
            ref readonly DecompositionEntry entry = ref Entries[slot];
            bool fromRoot = entry.SourcePosition == RootSource;
            switch (entry.Source)
            {
                case EntrySource.AtPosition:
                    return fromRoot ? BoundaryNode.Move(ref Root) : reader.TakeBoundaryNode(path, entry.SourcePosition);
                case EntrySource.LeftLeafOf:
                case EntrySource.RightLeafOf:
                    bool right = entry.Source == EntrySource.RightLeafOf;
                    return fromRoot ? Root.InlineLeaf(right) : reader.TakeInlineLeaf(path, entry.SourcePosition, right);
                default:
                    throw new InvalidOperationException("A composed result is not read back as a boundary node.");
            }
        }

        /// <summary>Takes the fold's result at <paramref name="slot"/>.</summary>
        internal FoldResult TakeResult(int slot) => FoldResult.Move(ref _results![slot]);

        internal void Set(int slot, ref FoldResult result)
        {
            Entries[slot] = default;
            (_results ??= ArrayPool<FoldResult>.Shared.Rent(PbtFourLevelGroupGeometry.BoundarySlots))[slot] = FoldResult.Move(ref result);
        }

        /// <summary>Returns the results' array once composition has taken every live one, which leaves it clear.</summary>
        internal void ReturnResults()
        {
            if (_results is null) return;
            ArrayPool<FoldResult>.Shared.Return(_results);
            _results = null;
        }
    }

    /// <summary>The source position standing for <see cref="Frontier.Root"/>, which no group position addresses.</summary>
    internal const int RootSource = PbtNodeGroupCodec.PositionCount;

    /// <summary>Where a frontier slot reads its node from.</summary>
    internal enum EntrySource : byte
    {
        /// <summary>The entry carries a composed node of its own.</summary>
        Node,
        /// <summary>The node stored at the source position, or the group's root.</summary>
        AtPosition,
        /// <summary>The leaf inlined as the left child of the node at the source position.</summary>
        LeftLeafOf,
        /// <summary>The leaf inlined as the right child of the node at the source position.</summary>
        RightLeafOf,
    }

    [InlineArray(PbtFourLevelGroupGeometry.BoundarySlots)]
    internal struct EntryBuffer { private DecompositionEntry _element; }
}
