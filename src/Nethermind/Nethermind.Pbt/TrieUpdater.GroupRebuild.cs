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
        private readonly record struct FoldedNode(ResultKind Kind, int Offset)
        {
            private const int NoEntry = -1;

            public static FoldedNode Absent => new(ResultKind.Absent, NoEntry);

            public static FoldedNode Stored(ResultKind kind, int offset) => new(kind, offset);

            public static FoldedNode Unstored => new(ResultKind.Internal, NoEntry);

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
        /// Whether the slot's blob is a group or a run does not enter into it: a run's cached node hash
        /// is a boundary internal's hash, so the entry either way is the same 32 bytes, and the blob
        /// itself is settled outside the encoding. A frame with no existing encoding — a pushed-stem
        /// subtree or a run — has nothing to copy, and a single slot is one entry either way.
        /// </para>
        /// </remarks>
        /// <param name="format">
        /// The encoding being written. A run is only ever what a fold in that very format produced, so a
        /// group in the other one is refolded in full instead — which is what converts it on the way
        /// past. A skipped position heads no run either: it has no entry, and
        /// <see cref="PbtTrieNodeGroup.Builder.AppendSubtree"/> takes a run's last entry for its root.
        /// Its children are stored, so they are copied in its place and it is folded from them.
        /// </param>
        private static bool IsCleanRange(
            PbtTrieNodeGroup existing, uint changed, uint range, int position, int width, PbtGroupFormat format) =>
            width > 1
            && (changed & range) == 0
            && !existing.IsEmpty
            && existing.Format == format
            && PbtTrieNodeGroup.StoresInternalAtWidth(format, width)
            && existing.KindAt(position) == NodeKind.Internal;

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
            uint occupied, uint stems, uint changedMask, Span<byte> destination, PbtGroupFormat format)
        {
            private readonly ReadOnlySpan<(int Slot, Boundary Node)> _changed = changed;
            private readonly PbtTrieNodeGroup _existing = existing;
            private readonly uint _occupied = occupied;
            private readonly uint _stems = stems;
            private readonly uint _changedMask = changedMask;
            private readonly PbtGroupFormat _format = format;
            private PbtTrieNodeGroup.Builder _builder = new(destination, format);
            private int _cursor;

            /// <summary>
            /// The value at boundary <paramref name="slot"/>, taken from the sorted changed span when the
            /// slot changed, and read back out of the existing encoding — at its fixed boundary position —
            /// when it did not. The changed span is consumed in slot order, which the walk visits it in.
            /// </summary>
            private Boundary ResolveCombinedNodeAtBoundary(int slot)
            {
                if (_existing.IsEmpty || (_changedMask >> slot & 1) != 0)
                {
                    Debug.Assert(_cursor < _changed.Length && _changed[_cursor].Slot == slot, "the span's boundaries are consumed in slot order");
                    return _changed[_cursor++].Node;
                }

                PbtTrieNodeGroup.Slot slotNode = _existing[PbtTrieNodeGroup.BoundaryPosition(slot)];
                return new Boundary(slotNode.Hash, (_stems >> slot & 1) != 0 ? slotNode.Stem : default);
            }

            /// <summary>
            /// Folds the boundary values over <c>[firstSlot, firstSlot + width)</c> into the node at
            /// <paramref name="position"/>, appending it and every node below it to the blob, and outputs
            /// via <paramref name="hash"/> the node hash it contributes to its parent.
            /// </summary>
            /// <remarks>
            /// The rebuild's one entry point: <see cref="RebuildGroup"/> calls it for the whole group at
            /// <see cref="PbtTrieNodeGroup.RootPosition"/>, and every other call is this recursing into its
            /// own halves. Post-order, so a node is appended only once all of its children are.
            /// </remarks>
            public FoldedNode Fold(int position, int firstSlot, int width, out ValueHash256 hash)
            {
                uint range = ((1u << width) - 1) << firstSlot;
                uint occupied = _occupied & range;
                switch (KindOf(occupied, _stems))
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
                                return FoldedNode.Stored(ResultKind.Stem, _builder.AppendStem(position, stem.Stem, stem.Hash));
                            }

                            int stemHalf = width / 2;
                            return (occupied & (((1u << stemHalf) - 1) << firstSlot)) != 0
                                ? Fold(position - width, firstSlot, stemHalf, out hash)
                                : Fold(position - 1, firstSlot + stemHalf, stemHalf, out hash);
                        }
                }

                if (IsCleanRange(_existing, _changedMask, range, position, width, _format))
                {
                    _existing.SubtreeBitmaps(position, width, out uint presence, out uint stems);
                    hash = _existing[position].NodeHash();
                    return FoldedNode.Stored(
                        ResultKind.Internal,
                        _builder.AppendSubtree(presence, stems, _existing.SubtreeEntries(position, width, presence, stems)));
                }

                // a boundary slot: its internal node is the cached pointer to the child blob, which for a
                // run is the run's own cached node hash — so a run reads here exactly as a group does
                if (width == 1)
                {
                    Boundary boundary = ResolveCombinedNodeAtBoundary(firstSlot);
                    hash = boundary.Hash;
                    return FoldedNode.Stored(ResultKind.Internal, _builder.AppendInternal(position, boundary.Hash));
                }

                int half = width / 2;
                Fold(position - width, firstSlot, half, out ValueHash256 leftHash);
                Fold(position - 1, firstSlot + half, half, out ValueHash256 rightHash);
                hash = Blake3Hash.HashPairOrZero(leftHash, rightHash);

                // The hash is folded either way — the root depends on every level of it. A format that
                // skips this level, and the group root whatever the format (the parent caches it in its
                // boundary slot), simply does not write it down, handing it to the parent instead.
                bool storeHere = PbtTrieNodeGroup.StoresInternalAtWidth(_format, width) && width != PbtTrieNodeGroup.BoundarySlots;
                return storeHere
                    ? FoldedNode.Stored(ResultKind.Internal, _builder.AppendInternal(position, hash))
                    : FoldedNode.Unstored;
            }

            /// <inheritdoc cref="PbtTrieNodeGroup.Builder.Finish"/>
            public readonly int Finish(in PbtSubtreeStats stats) => _builder.Finish(stats);
        }
    }
}
