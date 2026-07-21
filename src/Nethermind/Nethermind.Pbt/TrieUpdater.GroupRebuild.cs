// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using Nethermind.Core.Crypto;
using NodeKind = Nethermind.Pbt.PbtTrieNodeGroup.NodeKind;

namespace Nethermind.Pbt;

public static partial class TrieUpdater
{
    private sealed partial class Updater
    {
        /// <summary>
        /// What the rebuild folded a node into, as its parent needs it: a <see cref="NodeRef"/> into the
        /// encoding being built where the group stores an entry for it, or the mark that it stores none —
        /// the node's hash coming from <see cref="GroupRebuild.Fold"/>'s output rather than an entry.
        /// </summary>
        /// <remarks>
        /// A <see cref="PbtGroupFormat.Interleaved"/> group stores no internal node at an odd level, and no
        /// group stores its own internal root (the parent caches it in its boundary slot), so there is no
        /// entry for the parent to read the hash back out of; it takes the hash the fold hands up instead.
        /// The offset alone tells a stored node from such an unstored one (<see cref="IsStored"/>).
        /// </remarks>
        private readonly record struct FoldedNode(NodeKind Kind, int Offset)
        {
            private const int NoEntry = -1;

            public static FoldedNode Absent => new(NodeKind.Absent, NoEntry);

            public static FoldedNode Stored(NodeKind kind, int offset) => new(kind, offset);

            public static FoldedNode Unstored => new(NodeKind.Internal, NoEntry);

            public bool IsStored => Offset != NoEntry;
        }

        /// <summary>
        /// Whether the internal node at <paramref name="position"/>, over the boundary slots
        /// <paramref name="range"/>, folds to exactly the nodes <paramref name="existing"/> already
        /// encodes — so that its entries are copied out of that encoding rather than rebuilt.
        /// </summary>
        /// <remarks>
        /// The fold is a pure function of a range's boundary results, and every stored group is some
        /// fold's output, so a range whose results are the ones this very group was built from folds
        /// back to the bytes already there. That is what <paramref name="changed"/> says: an untouched
        /// slot's result is the occupant routed straight out of <paramref name="existing"/>, and a
        /// touched one that reports unchanged came back the same node — the same kind, hash and stem —
        /// since a change of any of them moves the node hash <c>changed</c> is decided on.
        /// <para>
        /// A slot holding a run is no different: its entry is the run's own encoding, which an unchanged
        /// slot's is byte for byte. A frame with no existing encoding — a pushed-stem subtree or a run —
        /// has nothing to copy, and a single slot is one entry either way.
        /// </para>
        /// </remarks>
        /// <param name="format">
        /// The encoding being written. A clean range is only ever what a fold in that very format
        /// produced, so a group in the other one is refolded in full instead — which is what converts it
        /// on the way past. A skipped position heads no such range either: it has no entry, and
        /// <see cref="PbtTrieNodeGroup.Builder.AppendSubtree"/> takes the last entry of a range for its
        /// root. Its children are stored, so they are copied in its place and it is folded from them.
        /// </param>
        private static bool IsCleanRange(
            PbtTrieNodeGroup existing, uint changed, uint rangeBitmask, int position, int width, PbtGroupFormat format) =>
            width > 1
            && (changed & rangeBitmask) == 0
            && !existing.IsEmpty
            && existing.Format == format
            && PbtLayout.TrieNodeGroupStoresInternalAtWidth(format, width)
            && (existing.KindAt(position) == NodeKind.Internal || position == PbtLayout.TrieNodeGroupRootPosition);

        /// <summary>
        /// Rebuilds a group's nodes from its sixteen boundary values directly into the group's final
        /// encoding.
        /// </summary>
        /// <remarks>
        /// Mirrors <see cref="StemLeafBlob.RebuildState"/>: a single post-order walk appends each node
        /// to the blob as it is folded, with no intermediate set of nodes and no separate encoding
        /// pass. This is well-defined because post-order visits a group's positions in ascending
        /// order — exactly the order the encoding stores them in — so each entry's offset is implicit
        /// in the append cursor, and a subtree's entries are one unbroken run. That run is what lets
        /// the walk prune at a range no write changed and copy it over whole, as the leaf blob's
        /// rebuild copies a clean leaf subtree. The walk hands each subtree up as a
        /// <see cref="FoldedNode"/>, a reference into the buffer, so a node's hash is materialized only
        /// where its parent needs it rather than copied by value up the stack — bar a node the format
        /// stores no entry for, whose hash has nowhere else to live.
        /// <para>
        /// Node kinds come from <see cref="KindOf"/> rather than from the walk, which is what lets a
        /// node be appended at its own position instead of by its parent: a stem never recurses, so
        /// every node reached below the root has an internal parent and is therefore materialized.
        /// The two shapes that are not — an empty group and a stem hoisting out of a non-root group —
        /// encode to nothing and are settled by the caller before the walk starts.
        /// </para>
        /// </remarks>
        private ref struct GroupRebuild(
            ReadOnlySpan<(int Slot, Boundary Node)> changed, PbtTrieNodeGroup existing,
            NodeGroupBitmasks boundary, uint changedBitmask, in ValueHash256 rootHash, Span<byte> destination,
            PbtGroupFormat format)
        {
            private readonly ReadOnlySpan<(int Slot, Boundary Node)> _changed = changed;
            private readonly PbtTrieNodeGroup _existing = existing;
            private readonly NodeGroupBitmasks _boundary = boundary;
            private readonly uint _changedBitmask = changedBitmask;
            private readonly ValueHash256 _rootHash = rootHash;
            private readonly PbtGroupFormat _format = format;
            private PbtTrieNodeGroup.Builder _builder = new(destination, format);
            private int _cursor;

            /// <summary>
            /// Whether boundary <paramref name="slot"/>'s value comes from the descent rather than from the
            /// encoding being replaced, which has none to give when the frame was seeded.
            /// </summary>
            private readonly bool IsResolvedFromResults(int slot) => _existing.IsEmpty || (_changedBitmask >> slot & 1) != 0;

            /// <summary>
            /// The value at boundary <paramref name="slot"/>, taken from the sorted changed span when the
            /// slot changed, and read back out of the existing encoding — at its fixed boundary position —
            /// when it did not. The changed span is consumed in slot order, which the walk visits it in.
            /// </summary>
            private Boundary ResolveCombinedNodeAtBoundary(int slot)
            {
                if (IsResolvedFromResults(slot))
                {
                    Debug.Assert(_cursor < _changed.Length && _changed[_cursor].Slot == slot, "the span's boundaries are consumed in slot order");
                    return _changed[_cursor++].Node;
                }

                PbtTrieNodeGroup.Slot slotNode = _existing[PbtLayout.TrieNodeGroupBoundarySlotPosition(slot)];
                return new Boundary(slotNode.Hash, (_boundary.Stems >> slot & 1) != 0 ? slotNode.Stem : default, Chain: null);
            }

            /// <summary>
            /// Folds the boundary values over <c>[firstSlot, firstSlot + width)</c> into the node at
            /// <paramref name="position"/>, appending it and every node below it to the blob, and outputs
            /// via <paramref name="hash"/> the node hash it contributes to its parent.
            /// </summary>
            /// <remarks>
            /// The rebuild's one entry point: <see cref="RebuildGroup"/> calls it for the whole group at
            /// <see cref="PbtLayout.TrieNodeGroupRootPosition"/>, and every other call is this recursing into its
            /// own halves. Post-order, so a node is appended only once all of its children are.
            /// </remarks>
            public FoldedNode Fold(int position, int firstSlot, int width, out ValueHash256 hash)
            {
                uint rangeBitmask = ((1u << width) - 1) << firstSlot;
                uint occupiedBitmask = _boundary.Presence & rangeBitmask;
                switch (KindOf(occupiedBitmask, _boundary.Stems))
                {
                    case NodeKind.Absent:
                        hash = default;
                        return FoldedNode.Absent;
                    case NodeKind.Stem:
                        {
                            // a lone stem lands at its boundary position, not its shortest unique prefix: a
                            // single-child level is no node of the stem trie, so the fold recurses down to the
                            // boundary, stores the stem there and passes its node hash up unchanged. That keeps
                            // a stem's slot fixed however its siblings shift, which is what lets an unchanged
                            // boundary be read back out of the existing encoding.
                            if (width == 1)
                            {
                                Boundary stem = ResolveCombinedNodeAtBoundary(firstSlot);
                                hash = StemLeafBlob.ComputeStemNodeHash(stem.Stem, stem.Hash);
                                return FoldedNode.Stored(NodeKind.Stem, _builder.AppendStem(position, stem.Stem, stem.Hash));
                            }

                            int stemHalf = width / 2;
                            return (occupiedBitmask & (((1u << stemHalf) - 1) << firstSlot)) != 0
                                ? Fold(position - width, firstSlot, stemHalf, out hash)
                                : Fold(position - 1, firstSlot + stemHalf, stemHalf, out hash);
                        }
                }

                if (IsCleanRange(_existing, _changedBitmask, rangeBitmask, position, width, _format))
                {
                    // The group's own root stores no entry to read a hash back out of, so it takes the one
                    // the parent cached — which is what lets a wholly unchanged group copy over with no
                    // hashing at all, rather than refolding this level from its halves.
                    bool isGroupRoot = position == PbtLayout.TrieNodeGroupRootPosition;
                    NodeGroupBitmasks clean = _existing.SubtreeBitmaps(position, width);
                    hash = isGroupRoot ? _rootHash : _existing[position].NodeHash();
                    int offset = _builder.AppendSubtree(clean, _existing.SubtreeEntries(position, width, clean));
                    return isGroupRoot ? FoldedNode.Unstored : FoldedNode.Stored(NodeKind.Internal, offset);
                }

                // a boundary slot: its internal node is the cached pointer to the child blob
                if (width == 1)
                {
                    Boundary boundary = ResolveCombinedNodeAtBoundary(firstSlot);
                    hash = boundary.Hash;
                    if ((_boundary.Chains >> firstSlot & 1) == 0)
                    {
                        return FoldedNode.Stored(NodeKind.Internal, _builder.AppendInternal(position, boundary.Hash));
                    }

                    // A run is held outright rather than pointed at, so its entry is its whole encoding —
                    // copied from wherever this frame has it: the one the descent produced, or the one the
                    // group being replaced already holds. What it contributes above is its cached node
                    // hash, which is what a pointer's entry is, so the fold reads the two alike.
                    Debug.Assert(!IsResolvedFromResults(firstSlot) || boundary.Chain is not null, "a run the descent settled rides in with its own encoding");
                    ReadOnlySpan<byte> chain = boundary.Chain is { } rebuilt ? rebuilt.GetSpan() : _existing[position].ChainData;
                    return FoldedNode.Stored(NodeKind.Chain, _builder.AppendChain(position, chain));
                }

                int half = width / 2;
                Fold(position - width, firstSlot, half, out ValueHash256 leftHash);
                Fold(position - 1, firstSlot + half, half, out ValueHash256 rightHash);
                hash = Blake3Hash.HashPairOrZero(leftHash, rightHash);

                // The hash is folded either way — the root depends on every level of it. A format that
                // skips this level, and the group root whatever the format (the parent caches it in its
                // boundary slot), simply does not write it down, handing it to the parent instead.
                bool storeHere = PbtLayout.TrieNodeGroupStoresInternalAtWidth(_format, width) && width != PbtLayout.TrieNodeGroupBoundarySlots;
                return storeHere
                    ? FoldedNode.Stored(NodeKind.Internal, _builder.AppendInternal(position, hash))
                    : FoldedNode.Unstored;
            }

            /// <inheritdoc cref="PbtTrieNodeGroup.Builder.Finish"/>
            public readonly int Finish(in PbtSubtreeStats stats) => _builder.Finish(stats);
        }
    }
}
