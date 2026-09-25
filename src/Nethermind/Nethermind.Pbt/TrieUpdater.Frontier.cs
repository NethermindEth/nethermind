// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using Nethermind.Core.Crypto;

namespace Nethermind.Pbt;

internal static partial class TrieUpdater<TKey, TPath>
    where TKey : unmanaged, IPbtKey<TKey>
    where TPath : struct, IPbtNodePath<TPath>
{
    /// <summary>The boundary slots of the group being rebuilt.</summary>
    /// <remarks>
    /// Untouched entries are filled by <see cref="Decompose"/> as it resolves the existing node group, a touched entry
    /// when <see cref="TakeBoundary"/> hands its node to the fold that replaces it, then all are taken back by
    /// <see cref="Compose"/> as it rebuilds. A decomposed entry names
    /// where its node is read from rather than holding it, so only the folds' own results are carried here. A stored
    /// node no touched slot lies under has no entry, since composition copies it without consulting the frontier.
    /// <see cref="Mask"/> marks the positions that hold a node. Taking one leaves its bit set: <see cref="SetBoundary"/>
    /// rewrites a folded slot's bit, and composition visits each position once.
    /// </remarks>
    /// <param name="touchedMask">The slots the folds replace, whose results are held by rank in a span sized to them.</param>
    internal struct Frontier(int touchedMask)
    {
        internal EntryBuffer Entries;
        internal uint Mask;
        /// <summary>The stored positions with no touched slot under them, which <see cref="Compose"/> copies straight from the frame.</summary>
        internal uint Copies;
        /// <summary>The positions below the group root that the group stores a node at.</summary>
        internal uint Stored;
        /// <summary>The touched slots whose entries are resolved only once their fold takes them.</summary>
        internal int Unresolved;
        /// <summary>The input node, for the slots that resolve to the group's own root instead of a node inside it.</summary>
        internal BoundaryNode Root;
        private readonly uint _touchedMask = (uint)touchedMask;

        /// <summary>Records that <paramref name="slot"/> is read from <paramref name="source"/>, occupying <paramref name="position"/>.</summary>
        internal void Place(int slot, int position, EntrySource source, int sourcePosition)
        {
            Entries[slot] = new DecompositionEntry(source, sourcePosition);
            Mask |= 1u << position;
        }

        /// <summary>Takes the boundary node at <paramref name="slot"/>, resolving it against the frame it was read from.</summary>
        internal BoundaryNode TakeBoundaryNode<TFrame>(scoped ref TFrame reader, scoped ref StoredGroupHashes hashes, int slot, TrieUpdaterMetrics? metrics)
            where TFrame : struct, IGroupFrame<TKey, TPath>
        {
            ref readonly DecompositionEntry entry = ref Entries[slot];
            bool fromRoot = entry.SourcePosition == RootSource;
            switch (entry.Source)
            {
                case EntrySource.AtPosition:
                    // The root stays: a touched slot resolved after this take still reads its links from it.
                    if (fromRoot) return Root;
                    ValueHash256 hash = LinkHash(ref reader, this, entry.SourcePosition);
                    if (hash == default) hash = hashes.GetHash(ref reader, entry.SourcePosition, metrics);
                    return reader.TakeBoundaryNode(entry.SourcePosition, hash);
                case EntrySource.LeftLeafOf:
                case EntrySource.RightLeafOf:
                    bool right = entry.Source == EntrySource.RightLeafOf;
                    return fromRoot ? Root.InlineLeaf(right) : reader.TakeInlineLeaf(entry.SourcePosition, right);
                default:
                    throw new InvalidOperationException("A composed result is not read back as a boundary node.");
            }
        }

        /// <summary>Takes the fold's result at <paramref name="slot"/>.</summary>
        internal readonly void TakeResult(scoped Span<FoldResult> results, int slot, ref FoldResult result) => FoldResult.Move(ref results[ResultIndex(slot)], ref result);

        internal void Set(scoped Span<FoldResult> results, int slot, ref FoldResult result)
        {
            Entries[slot] = default;
            FoldResult.Move(ref result, ref results[ResultIndex(slot)]);
        }

        private readonly int ResultIndex(int slot)
        {
            Debug.Assert((_touchedMask >> slot & 1) != 0, "Only a touched slot holds a fold's result.");
            return BitOperations.PopCount(_touchedMask & ((1u << slot) - 1));
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
