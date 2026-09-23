// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
    /// </remarks>
    internal struct Frontier
    {
        internal EntryBuffer Entries;
        internal uint Mask;
        /// <summary>The input node, for the slots that resolve to the group's own root instead of a node inside it.</summary>
        internal BoundaryNode Root;

        /// <summary>Records that <paramref name="slot"/> is read from <paramref name="source"/>, occupying <paramref name="position"/>.</summary>
        internal void Place(int slot, int position, EntrySource source, int sourcePosition)
        {
            Entries[slot] = new DecompositionEntry(source, sourcePosition);
            Mask |= 1u << position;
        }

        /// <summary>Takes the boundary node at <paramref name="slot"/>, resolving it against the frame it was read from.</summary>
        internal BoundaryNode TakeBoundaryNode(scoped ref GroupFrameReader<TKey, TPath> reader, scoped in PbtTraversalPath path, int slot)
        {
            DecompositionEntry entry = Entries[slot];
            Entries[slot] = default;
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

        /// <summary>Takes the entry at <paramref name="slot"/> for composition, as one of the three things a frontier holds.</summary>
        /// <returns>
        /// A fold's composed result or the direct copy of the stored node the entry names, or default with
        /// <paramref name="boundary"/> set when the entry is a boundary node: the input node, or a leaf that it or a
        /// stored branch inlines. What each becomes at the cursor is composition's to derive.
        /// </returns>
        internal TraversalSubtree Take(scoped ref GroupFrameReader<TKey, TPath> reader, PbtTraversalPath path, int slot, out BoundaryNode boundary)
        {
            DecompositionEntry entry = Entries[slot];
            switch (entry.Source)
            {
                case EntrySource.Node:
                    Entries[slot] = default;
                    boundary = default;
                    return new TraversalSubtree(path, Subtree.Move(ref entry.Node));
                case EntrySource.AtPosition when entry.SourcePosition != RootSource:
                    Entries[slot] = default;
                    boundary = default;
                    return new TraversalSubtree(path, reader.TakeDirectCopy(path, entry.SourcePosition));
                default:
                    boundary = TakeBoundaryNode(ref reader, path, slot);
                    return default;
            }
        }

        internal void Set(int slot, ref TraversalSubtree result)
        {
            Debug.Assert(result.Copy.IsEmpty, "A fold result is composed, never a node still borrowed from a frame.");
            Entries[slot] = new DecompositionEntry(ref result.Node);
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
