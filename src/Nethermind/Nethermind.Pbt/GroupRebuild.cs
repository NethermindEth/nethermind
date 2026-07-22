// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Diagnostics;
using System.Numerics;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using NodeKind = Nethermind.Pbt.PbtTrieNodeGroup.NodeKind;

namespace Nethermind.Pbt;

/// <summary>
/// A boundary node's value as the fold consumes it: the hash it contributes, its stem when it is a
/// stem node, and the memory holding its encoding when it is a run, which the group copies in
/// verbatim. Projected from the descent's results once, so the fold reads plain values rather than
/// reaching back through their leases.
/// </summary>
internal readonly record struct Boundary(ValueHash256 Hash, Stem Stem, RefCountingMemory? Chain);

/// <summary>
/// What the rebuild folded a node into, as its parent needs it: the offset of its entry in the
/// encoding being built where the group stores one for it, or the mark that it stores none —
/// the node's hash coming from <see cref="GroupRebuild.Fold"/>'s output rather than an entry.
/// </summary>
/// <remarks>
/// A <see cref="PbtGroupFormat.Interleaved"/> group stores no internal node at an odd level, and no
/// group stores its own internal root (the parent caches it in its boundary slot), so there is no
/// entry for the parent to read the hash back out of; it takes the hash the fold hands up instead.
/// The offset alone tells a stored node from such an unstored one (<see cref="IsStored"/>).
/// </remarks>
internal readonly record struct FoldedNode(NodeKind Kind, int Offset)
{
    private const int NoEntry = -1;

    public static FoldedNode Absent => new(NodeKind.Absent, NoEntry);

    public static FoldedNode Stored(NodeKind kind, int offset) => new(kind, offset);

    public static FoldedNode Unstored => new(NodeKind.Internal, NoEntry);

    public bool IsStored => Offset != NoEntry;
}

/// <summary>
/// Rebuilds a group's nodes from its sixteen boundary values directly into the group's final
/// encoding, on the writer each of its calls is handed.
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
/// The writer is passed per call rather than held, as <see cref="PbtTrieNodeWrapper.Builder"/>'s is,
/// and every entry is committed to it as it is written. What the rebuild carries between calls is the
/// append cursor and the bitmaps, nothing borrowed — which is what lets it take the writer by
/// <c>ref</c> at all, and why <paramref name="rootHash"/> comes by value: a ref struct constructed
/// from a reference is bounded by that reference's scope, however plainly the reference is copied.
/// </para>
/// <para>
/// Node kinds come from <see cref="NodeGroupBitmasks.KindOf"/> rather than from the walk, which is what lets a
/// node be appended at its own position instead of by its parent: a stem never recurses, so
/// every node reached below the root has an internal parent and is therefore materialized.
/// The two shapes that are not — an empty group and a stem hoisting out of a non-root group —
/// encode to nothing and are settled by the caller before the walk starts.
/// </para>
/// <para>
/// Post-order is also what makes each entry's offset implicit in the append cursor, so no offset
/// math is needed while building, and an appended entry keeps a stable offset for the fold to read
/// back. The bitmaps are only known once the walk is done, which is
/// why they sit in the trailer rather than at the front: <see cref="Finish"/> appends them where
/// the entries ended, leaving the whole encoding a single forward pass. Backfilling a leading
/// header instead would write back over bytes the write has already streamed past, which no
/// prefetcher helps with. The format byte rides along with them so that an encoding ends with its
/// own identifier and its entries start at offset zero, which is what lets a container hold one
/// verbatim.
/// </para>
/// </remarks>
internal ref struct GroupRebuild(
    ReadOnlySpan<(int Slot, Boundary Node)> changed, PbtTrieNodeGroup existing,
    NodeGroupBitmasks boundary, uint changedBitmask, ValueHash256 rootHash, PbtGroupFormat format)
{
    private readonly ReadOnlySpan<(int Slot, Boundary Node)> _changed = changed;
    private readonly PbtTrieNodeGroup _existing = existing;
    private readonly NodeGroupBitmasks _boundary = boundary;
    private readonly uint _changedBitmask = changedBitmask;
    private readonly ValueHash256 _rootHash = rootHash;
    private readonly PbtGroupFormat _format = format;

    /// <summary>The room the whole encoding may need, which each write asks the rest of.</summary>
    /// <remarks>
    /// Asking for what is left of the bound rather than for the entry about to be added is what has the
    /// buffer grown once for the group instead of a step at a time: the first write sizes it for the
    /// whole encoding, and no later one can outrun what that left.
    /// </remarks>
    private readonly int _room = PbtTrieNodeGroup.EncodedLengthBound(boundary);

    private uint _presence;
    private uint _stems;
    private uint _chains;
    private int _offset = PbtTrieNodeGroup.EntriesOffset;
    private int _changedCursor;

    /// <summary>
    /// Whether the internal node at <paramref name="position"/>, over the boundary slots
    /// <paramref name="rangeBitmask"/>, folds to exactly the nodes <paramref name="existing"/> already
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
    /// the last entry of a range is taken for its root. Its children are stored, so they are copied in
    /// its place and it is folded from them.
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
            Debug.Assert(_changedCursor < _changed.Length && _changed[_changedCursor].Slot == slot, "the span's boundaries are consumed in slot order");
            return _changed[_changedCursor++].Node;
        }

        PbtTrieNodeGroup.Slot slotNode = _existing[PbtLayout.TrieNodeGroupBoundarySlotPosition(slot)];
        return new Boundary(slotNode.Hash, (_boundary.Stems >> slot & 1) != 0 ? slotNode.Stem : default, Chain: null);
    }

    /// <summary>
    /// Folds the whole group into <paramref name="writer"/> and closes the encoding off.
    /// </summary>
    /// <returns>
    /// What the group folds to, as its parent holds it — a stem root read back out of the fresh
    /// encoding, or, the usual case, an internal root the encoding stores no entry for — that root's
    /// node hash, and the length written.
    /// </returns>
    /// <param name="stats">What the whole subtree amounts to, for the group's trailer; see <see cref="Finish"/>.</param>
    public (PbtTrieNodeGroup.ValueSlot Root, ValueHash256 RootHash, int Length) Rebuild(
        ref BufferWriter writer, in PbtSubtreeStats stats)
    {
        // an internal group root is folded to a by-value hash and never stored, the parent caching it in
        // its boundary slot; only a stem root is written, and is read back out of the entries — the last
        // bytes the writer holds — before the trailer joins them
        FoldedNode root = Fold(ref writer, PbtLayout.TrieNodeGroupRootPosition, 0, PbtLayout.TrieNodeGroupBoundarySlots, out ValueHash256 rootHash);
        PbtTrieNodeGroup.ValueSlot rootSlot = PbtTrieNodeGroup.InternalSlot(rootHash);
        if (root.IsStored)
        {
            Debug.Assert(root.Kind == NodeKind.Stem, "an internal root is folded, never stored");

            rootSlot = PbtTrieNodeGroup.SlotAt(writer.WrittenSpan[^_offset..], root.Kind, root.Offset).ToValue();
        }

        return (rootSlot, rootHash, Finish(ref writer, stats));
    }

    /// <summary>
    /// Folds the boundary values over <c>[firstSlot, firstSlot + width)</c> into the node at
    /// <paramref name="position"/>, appending it and every node below it to the blob, and outputs
    /// via <paramref name="hash"/> the node hash it contributes to its parent.
    /// </summary>
    /// <remarks>
    /// <see cref="Rebuild"/> calls it for the whole group at
    /// <see cref="PbtLayout.TrieNodeGroupRootPosition"/>, and every other call is this recursing into its
    /// own halves. Post-order, so a node is appended only once all of its children are.
    /// </remarks>
    private FoldedNode Fold(ref BufferWriter writer, int position, int firstSlot, int width, out ValueHash256 hash)
    {
        uint rangeBitmask = ((1u << width) - 1) << firstSlot;
        uint occupiedBitmask = _boundary.Presence & rangeBitmask;
        switch (_boundary.KindOf(rangeBitmask))
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

                        MarkPresent(position);
                        _stems |= 1u << position;
                        int stemOffset = Write(ref writer, stem.Stem.Bytes);
                        Write(ref writer, stem.Hash.Bytes);
                        return FoldedNode.Stored(NodeKind.Stem, stemOffset);
                    }

                    int stemHalf = width / 2;
                    return (occupiedBitmask & (((1u << stemHalf) - 1) << firstSlot)) != 0
                        ? Fold(ref writer, position - width, firstSlot, stemHalf, out hash)
                        : Fold(ref writer, position - 1, firstSlot + stemHalf, stemHalf, out hash);
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

            (uint cleanPresence, uint cleanStems, uint cleanChains) = clean;
            Debug.Assert(cleanPresence != 0, "an absent subtree has no entries to append");
            Debug.Assert((cleanStems & ~cleanPresence) == 0, "only a present position holds a stem");
            Debug.Assert(_presence >> BitOperations.TrailingZeroCount(cleanPresence) == 0, "nodes must be appended in ascending position order");
            Debug.Assert(
                !PbtLayout.TrieNodeGroupHoldsSkippedInternal(_format, cleanPresence, cleanStems),
                "a verbatim run must be in this group's own format");

            _presence |= cleanPresence;
            _stems |= cleanStems;
            _chains |= cleanChains;

            ReadOnlySpan<byte> entries = _existing.SubtreeEntries(position, width, clean);
            int offset = _offset + entries.Length - RootEntryLength(BitOperations.Log2(cleanPresence), clean);
            Write(ref writer, entries);
            return isGroupRoot ? FoldedNode.Unstored : FoldedNode.Stored(NodeKind.Internal, offset);
        }

        // a boundary slot: its internal node is the cached pointer to the child blob
        if (width == 1)
        {
            Boundary boundary = ResolveCombinedNodeAtBoundary(firstSlot);
            hash = boundary.Hash;
            if ((_boundary.Chains >> firstSlot & 1) == 0)
            {
                return FoldedNode.Stored(NodeKind.Internal, AppendInternal(ref writer, position, boundary.Hash));
            }

            // A run is held outright rather than pointed at, so its entry is its whole encoding —
            // copied from wherever this frame has it: the one the descent produced, or the one the
            // group being replaced already holds. What it contributes above is its cached node
            // hash, which is what a pointer's entry is, so the fold reads the two alike.
            Debug.Assert(!IsResolvedFromResults(firstSlot) || boundary.Chain is not null, "a run the descent settled rides in with its own encoding");
            ReadOnlySpan<byte> chain = boundary.Chain is { } rebuilt ? rebuilt.GetSpan() : _existing[position].ChainData;
            Debug.Assert(chain.Length == PbtTrieNodeGroup.Slot.ChainLength);
            Debug.Assert(position == PbtLayout.TrieNodeGroupBoundarySlotPosition(firstSlot), "a lone slot is its own boundary position, which is what its run is marked at");

            MarkPresent(position);
            _chains |= 1u << firstSlot;
            return FoldedNode.Stored(NodeKind.Chain, Write(ref writer, chain));
        }

        int half = width / 2;
        Fold(ref writer, position - width, firstSlot, half, out ValueHash256 leftHash);
        Fold(ref writer, position - 1, firstSlot + half, half, out ValueHash256 rightHash);
        hash = Blake3Hash.HashPairOrZero(leftHash, rightHash);

        // The hash is folded either way — the root depends on every level of it. A format that
        // skips this level, and the group root whatever the format (the parent caches it in its
        // boundary slot), simply does not write it down, handing it to the parent instead.
        bool storeHere = PbtLayout.TrieNodeGroupStoresInternalAtWidth(_format, width) && width != PbtLayout.TrieNodeGroupBoundarySlots;
        return storeHere
            ? FoldedNode.Stored(NodeKind.Internal, AppendInternal(ref writer, position, hash))
            : FoldedNode.Unstored;
    }

    /// <summary>Appends the internal node at <paramref name="position"/>, returning its entry offset.</summary>
    private int AppendInternal(ref BufferWriter writer, int position, in ValueHash256 hash)
    {
        Debug.Assert(hash != default, "internal nodes never cache a zero hash");
        Debug.Assert(!PbtLayout.TrieNodeGroupIsSkippedPosition(_format, position), "an interleaved group stores no internal node at a skipped level");
        MarkPresent(position);
        return Write(ref writer, hash.Bytes);
    }

    /// <summary>The length of the entry at <paramref name="rootPosition"/>, the last of an appended subtree's.</summary>
    private static int RootEntryLength(int rootPosition, NodeGroupBitmasks masks) =>
        (masks.Stems >> rootPosition & 1) != 0 ? PbtTrieNodeGroup.Slot.StemLength
        : PbtLayout.TrieNodeGroupIsBoundaryPosition(rootPosition) && (masks.Chains >> PbtLayout.TrieNodeGroupBoundarySlot(rootPosition) & 1) != 0 ? PbtTrieNodeGroup.Slot.ChainLength
        : PbtTrieNodeGroup.Slot.InternalLength;

    private void MarkPresent(int position)
    {
        Debug.Assert((uint)position < PbtLayout.TrieNodeGroupPositionCount);
        Debug.Assert(_presence >> position == 0, "nodes must be appended in ascending position order");
        _presence |= 1u << position;
    }

    /// <summary>Appends <paramref name="bytes"/> to the encoding and commits them, returning the offset they went to.</summary>
    private int Write(ref BufferWriter writer, ReadOnlySpan<byte> bytes)
    {
        int offset = _offset;
        bytes.CopyTo(writer.GetSpan(_room - offset));
        writer.Advance(bytes.Length);
        _offset = offset + bytes.Length;
        return offset;
    }

    /// <summary>
    /// Appends the trailer — the bitmaps, and the <paramref name="stats"/> of the subtree the group
    /// roots — commits the encoding to <paramref name="writer"/> and returns its length; 0 when nothing
    /// was appended, which the caller stores as a removal and the writer never sees.
    /// </summary>
    /// <param name="stats">
    /// The whole subtree's, which the walk that appended the nodes cannot know: the stems below a
    /// boundary slot the walk left alone are never read. The producer hoists it instead.
    /// </param>
    private readonly int Finish(ref BufferWriter writer, in PbtSubtreeStats stats)
    {
        if (_presence == 0) return 0;

        Span<byte> trailer = writer.GetSpan(_room - _offset);
        BinaryPrimitives.WriteUInt32LittleEndian(trailer[PbtTrieNodeGroup.PresenceTrailerOffset..], _presence);
        BinaryPrimitives.WriteUInt32LittleEndian(trailer[PbtTrieNodeGroup.StemsTrailerOffset..], _stems);
        BinaryPrimitives.WriteUInt16LittleEndian(trailer[PbtTrieNodeGroup.ChainsTrailerOffset..], (ushort)_chains);
        stats.Write(trailer[PbtTrieNodeGroup.StatsTrailerOffset..]);
        trailer[PbtTrieNodeGroup.FormatTrailerOffset] = (byte)_format;
        writer.Advance(PbtTrieNodeGroup.TrailerLength);

        return _offset + PbtTrieNodeGroup.TrailerLength;
    }
}
