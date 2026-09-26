// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Core.Threading;
using static Nethermind.Pbt.TrieUpdater;

namespace Nethermind.Pbt;

/// <summary>Applies complete-key mutations to a canonical compressed EIP-8297 tree.</summary>
public static partial class TrieUpdater
{
    internal static int GetBit(ReadOnlySpan<byte> bytes, int bit) => (bytes[bit >> 3] >> (7 - (bit & 7))) & 1;

    internal enum NodeKind : byte { Empty, Leaf, Branch }

    /// <summary>The bit flagging a composed branch's left child as a leaf.</summary>
    internal const byte LeftLeaf = 1;
    /// <summary>The bit flagging a composed branch's right child as a leaf.</summary>
    internal const byte RightLeaf = 2;

    /// <summary>Where a boundary node's complete key is read from, which is also whether it is a leaf at all.</summary>
    internal enum LeafSource : byte
    {
        /// <summary>Not a leaf: the encoding is the node's own branch, or there is none.</summary>
        None,
        /// <summary>The left child of the branch the encoding holds.</summary>
        ParentLeft,
        /// <summary>The right child of the branch the encoding holds.</summary>
        ParentRight,
        /// <summary>The encoding is this leaf's own, which only a single-leaf tree's root is stored as.</summary>
        Stored,
    }

    /// <summary>Groups consecutive buckets into runs, each holding the operations <paramref name="fanOut"/> asks of the descendants it absorbs.</summary>
    /// <remarks>
    /// A run's minimum follows its own stored descendants rather than the frame's, so a run over buckets with
    /// nothing stored below them stays CPU-bound however large its siblings are. A trailing shortfall joins the
    /// preceding run, so a frame that cannot fill two runs yields one.
    /// </remarks>
    /// <param name="counts">Operation counts per touched bucket, in ascending slot order.</param>
    /// <param name="descendantBytes">The stored size below each of those buckets, in the same order.</param>
    /// <returns>The number of runs; <paramref name="runEnds"/> holds each run's exclusive end bucket index.</returns>
    internal static int PlanBucketRuns(ReadOnlySpan<int> counts, ReadOnlySpan<long> descendantBytes, in FoldFanOut fanOut, Span<int> runEnds)
    {
        Debug.Assert(counts.Length == descendantBytes.Length, "Every touched bucket carries its stored size.");
        int runCount = 0;
        int sum = 0;
        long bytes = 0;
        for (int bucket = 0; bucket < counts.Length; bucket++)
        {
            sum += counts[bucket];
            bytes += descendantBytes[bucket];
            if (sum < fanOut.MinOperationsFor(bytes)) continue;
            runEnds[runCount++] = bucket + 1;
            sum = 0;
            bytes = 0;
        }
        if (runCount == 0) runCount = 1;
        runEnds[runCount - 1] = counts.Length;
        return runCount;
    }

    /// <summary>Charges a starting parallel-loop worker to <paramref name="quota"/>.</summary>
    /// <remarks>
    /// A loop starts only once its caller took one slot, the admission slot, which the first worker thread to start
    /// claims through <paramref name="admissionSlotClaimed"/>; every further worker takes quota outright, so the loop
    /// is charged exactly once per running worker, briefly exceeding the budget when sibling loops start together.
    /// The calling thread already holds its own slot and is not charged.
    /// </remarks>
    /// <returns>Whether the worker holds a slot to hand back through <see cref="ReturnWorkerQuota"/>.</returns>
    internal static bool TakeWorkerQuota(ConcurrencyController quota, int callerThreadId, ref int admissionSlotClaimed)
    {
        if (Environment.CurrentManagedThreadId == callerThreadId) return false;
        if (!ClaimAdmissionSlot(ref admissionSlotClaimed)) quota.TakeConcurrencyQuota();
        return true;
    }

    internal static void ReturnWorkerQuota(ConcurrencyController quota, bool taken)
    {
        if (taken) quota.ReturnConcurrencyQuota();
    }

    /// <summary>Returns the admission slot after the loop unless a worker claimed it and returns it itself.</summary>
    internal static void ReturnAdmissionSlot(ConcurrencyController quota, ref int admissionSlotClaimed)
    {
        if (ClaimAdmissionSlot(ref admissionSlotClaimed)) quota.ReturnConcurrencyQuota();
    }

    private static bool ClaimAdmissionSlot(ref int admissionSlotClaimed) => Interlocked.Exchange(ref admissionSlotClaimed, 1) == 0;

    internal static int BoundarySlot(ReadOnlySpan<byte> key, int groupDepth)
    {
        byte value = key[groupDepth >> 3];
        return (value >> (4 - (groupDepth & 4))) & 0x0F;
    }

}

internal static partial class TrieUpdater<TKey, TPath>
    where TKey : unmanaged, IPbtKey<TKey>
    where TPath : struct, IPbtNodePath<TPath>
{
    /// <summary>Publishes a frame's group and returns the size change of the group and everything folded below it.</summary>
    /// <remarks>
    /// Each stored descendant size is the loaded (or inherited) size of its slot adjusted by the change folded below that
    /// slot. A group that stays absent stores nothing, so only the folded change survives, and the store is not told
    /// to delete a group it never held.
    /// </remarks>
    internal static long PublishGroup<TFrame>(IPbtNodeGroupSink sink, ref TFrame reader, PbtNodeGroupWriter<TPath> writer,
        scoped in PbtTraversalPath path, in ValueHash256 hash)
        where TFrame : struct, IGroupFrame<TKey, TPath>
    {
        Span<long> descendantBytes = stackalloc long[PbtNodeGroupCodec.DescendantSlots];
        ushort candidateSlots = (ushort)(reader.DescendantMask | writer.DescendantDeltaMask);
        for (uint remaining = candidateSlots; remaining != 0; remaining &= remaining - 1)
        {
            int slot = BitOperations.TrailingZeroCount(remaining);
            descendantBytes[slot] = reader.DescendantBytes(slot) + writer.DescendantDelta(slot);
        }
        using RefCountingMemory? payload = writer.Detach(descendantBytes, candidateSlots);
        if (payload is not null || reader.PayloadLength != 0) sink.SetNodeGroup(path, hash, payload);
        return (payload?.GetSpan().Length ?? 0) - reader.PayloadLength + writer.DescendantDelta();
    }

    /// <summary>Whether the group at <paramref name="bitDepth"/> below <paramref name="current"/> is provably absent, so it is folded without reading the store.</summary>
    /// <remarks>
    /// A group holds the nodes strictly below its boundary node. Nothing is stored below an empty subtree or a leaf,
    /// and a branch over two inlined leaves is its own whole subtree, so none of the three owns a group. A branch whose
    /// children lie beyond this group owns no group either. Any other branch has a child inside the group, which is stored.
    /// </remarks>
    internal static bool IsAbsentGroup(scoped in BoundaryNode current, int bitDepth) => OwnsNoGroup(current) || IsAbsentGroupBelow(current, bitDepth);

    /// <summary>The frame of the absent group at <paramref name="bitDepth"/> below <paramref name="current"/>.</summary>
    /// <param name="spanningDescendantBytes">
    /// The size the owner recorded under the boundary slot on the way here, which a spanning branch carries down with it.
    /// </param>
    internal static AbsentGroupFrame<TKey, TPath> AbsentFrame(scoped in BoundaryNode current, scoped in PbtTraversalPath path, int bitDepth,
        long spanningDescendantBytes) =>
        // A spanning branch may inline two leaves as well; taking its slot size keeps the owner's accounting.
        IsAbsentGroupBelow(current, bitDepth)
            ? new(bitDepth, BranchSlot(current, path, bitDepth), spanningDescendantBytes)
            : new(bitDepth);

    /// <summary>Whether <paramref name="current"/>'s whole subtree is the node itself, which its owner group stores.</summary>
    /// <remarks>LeafChildrenMask decodes the branch encoding, so the empty and leaf kinds are ruled out first.</remarks>
    private static bool OwnsNoGroup(scoped in BoundaryNode current) =>
        current.IsEmpty || current.IsLeaf || current.LeafChildrenMask == (LeftLeaf | RightLeaf);

    /// <summary>The boundary slot of the group at <paramref name="bitDepth"/> that <paramref name="current"/>'s prefix passes through.</summary>
    internal static int BranchSlot(scoped in BoundaryNode current, scoped in PbtTraversalPath path, int bitDepth)
    {
        Debug.Assert(current.BranchDepth >= bitDepth + PbtFourLevelGroupGeometry.LevelsPerGroup);
        int slot = 0;
        for (int bit = bitDepth; bit < bitDepth + PbtFourLevelGroupGeometry.LevelsPerGroup; bit++)
            slot = (slot << 1) | current.PrefixBit(path, bit);
        return slot;
    }

    /// <summary>Whether <paramref name="current"/> is a branch whose children lie beyond the group at <paramref name="bitDepth"/>, so that group cannot exist.</summary>
    internal static bool IsAbsentGroupBelow(scoped in BoundaryNode current, int bitDepth)
    {
        Debug.Assert(bitDepth != 0, "The root group always exists.");
        return !current.IsEmpty && !current.IsLeaf && current.BranchDepth >= bitDepth + PbtFourLevelGroupGeometry.LevelsPerGroup;
    }

    /// <summary>Per-fold state shared by every frame of one root update.</summary>
    /// <remarks>
    /// <see cref="FoldQuota"/> and <see cref="Operations"/> are set only when wide frames may fold their buckets
    /// concurrently; the former is the budget every nested frame takes its extra workers from before fanning out, the latter
    /// is the backing array of every operation range, so a bucket can rebuild its span on another thread,
    /// and <see cref="FanOut"/> gives how many operations a concurrent run of buckets holds at least, by what it has stored below it.
    /// Groups are read from <see cref="Store"/> but published to <see cref="Writer"/>, which a concurrent bucket replaces
    /// with its own <see cref="IPbtStore.CreateWriter"/> so it never writes the store directly.
    /// </remarks>
    internal sealed class FoldContext(IPbtStore store, IPbtNodeGroupSink writer, IRefCountingMemoryProvider memoryProvider, ConcurrencyController? foldQuota, PbtWriteOperation<TKey>[]? operations, FoldFanOut fanOut, PbtPrefixlessBranchOmission prefixlessBranchOmission)
    {
        internal IPbtStore Store { get; } = store;
        internal IPbtNodeGroupSink Writer { get; } = writer;
        internal IRefCountingMemoryProvider MemoryProvider { get; } = memoryProvider;
        internal ConcurrencyController? FoldQuota { get; } = foldQuota;
        internal PbtWriteOperation<TKey>[]? Operations { get; } = operations;
        internal FoldFanOut FanOut { get; } = fanOut;
        internal PbtPrefixlessBranchOmission PrefixlessBranchOmission { get; } = prefixlessBranchOmission;

    }

    internal static int BoundaryPosition(int slot) => 2 * slot - BitOperations.PopCount((uint)slot);

    /// <summary>Takes the boundary node a fold is about to descend into, which <see cref="SetBoundary"/> then replaces with the fold's result.</summary>
    /// <remarks>A touched slot <see cref="Decompose"/> left unresolved is resolved here, as a block of its own.</remarks>
    internal static BoundaryNode TakeBoundary<TFrame>(scoped ref TFrame reader, scoped ref StoredGroupHashes hashes, scoped in PbtTraversalPath path,
        scoped ref Frontier frontier, int slot)
        where TFrame : struct, IGroupFrame<TKey, TPath>
    {
        if ((frontier.Unresolved >> slot & 1) != 0)
        {
            frontier.Unresolved &= ~(1 << slot);
            ResolveBlock(ref reader, path.BitDepth, frontier.Stored, frontier.Copies, slot, 1, ref frontier);
        }
        uint bit = 1u << BoundaryPosition(slot);
        if ((frontier.Mask & bit) == 0) return default;
        return frontier.TakeBoundaryNode(ref reader, ref hashes, slot);
    }

    internal static void SetBoundary(ref Frontier frontier, scoped Span<FoldResult> results, int slot, ref FoldResult result)
    {
        uint bit = 1u << BoundaryPosition(slot);
        frontier.Mask = result.IsEmpty ? frontier.Mask & ~bit : frontier.Mask | bit;
        frontier.Set(results, slot, ref result);
    }

    /// <summary>Resolves the input into opaque untouched siblings, leaving each touched slot for <see cref="TakeBoundary"/> to resolve.</summary>
    /// <remarks>
    /// Every entry is resolved from its own position upwards, against the deepest node the group stores above that
    /// position, or against the input when it stores none. That node's link is what both names the entry and holds its
    /// hash, so entries are placed without hashing anything. Entries use their leftmost boundary slot; the mask retains
    /// their group positions. An opaque subtree and its descendants never coexist, so their slots cannot collide. A
    /// stored node with no touched slot under it gets no entry: <see cref="Compose"/> copies it from the stored and
    /// touched positions alone.
    /// </remarks>
    [SkipLocalsInit]
    internal static void Decompose<TFrame>(ref TFrame reader, scoped in PbtTraversalPath path, ref BoundaryNode input,
        int bitDepth, ref Frontier frontier, int touchedMask)
        where TFrame : struct, IGroupFrame<TKey, TPath>
    {
        if (input.IsEmpty) return;
        frontier.Root = BoundaryNode.Move(ref input);
        int boundaryDepth = bitDepth + PbtFourLevelGroupGeometry.LevelsPerGroup;
        // A leaf, or a branch reaching past this group, is the whole subtree under one slot and needs no group read.
        if (frontier.Root.IsLeaf)
        {
            // The key is decoded once: a span taken straight off the property would point at an unnamed temporary.
            TKey rootLeafKey = frontier.Root.LeafKey;
            int leafSlot = BoundarySlot(rootLeafKey.Bytes, bitDepth);
            frontier.Place(leafSlot, BoundaryPosition(leafSlot), EntrySource.AtPosition, RootSource);
            return;
        }
        if (frontier.Root.BranchDepth >= boundaryDepth)
        {
            int spanningSlot = BranchSlot(frontier.Root, path, bitDepth);
            frontier.Place(spanningSlot, BoundaryPosition(spanningSlot), EntrySource.AtPosition, RootSource);
            return;
        }

        // The root is held by the frontier, not by the group, so its position never answers the climb.
        uint stored = reader.StoredPositions & ~(1u << PbtFourLevelGroupGeometry.RootPosition);
        uint copies = DirectCopyPositions(stored, touchedMask);
        frontier.Copies = copies;
        frontier.Stored = stored;
        frontier.Unresolved = touchedMask;
        for (int slot = 0; slot < PbtFourLevelGroupGeometry.BoundarySlots;)
        {
            // A touched slot is resolved when its fold takes it; everything else in the widest aligned untouched block it starts.
            if ((touchedMask >> slot & 1) != 0)
            {
                slot++;
                continue;
            }
            int width = 1;
            while (width < PbtFourLevelGroupGeometry.BoundarySlots
                   && (slot & (2 * width - 1)) == 0
                   && (touchedMask & (((1 << (2 * width)) - 1) << slot)) == 0)
                width <<= 1;
            ResolveBlock(ref reader, bitDepth, stored, copies, slot, width, ref frontier);
            slot += width;
        }

        // The stored positions below the group root with no touched slot under them, which composition copies unchanged.
        static uint DirectCopyPositions(uint stored, int touchedMask)
        {
            uint copies = 0;
            for (uint remaining = stored & ~(1u << PbtFourLevelGroupGeometry.RootPosition); remaining != 0; remaining &= remaining - 1)
            {
                int position = BitOperations.TrailingZeroCount(remaining);
                NodeGroupPath local = PbtFourLevelGroupGeometry.LocalPathOf(position);
                if ((touchedMask & (((1 << local.Width) - 1) << local.Slot)) == 0) copies |= 1u << position;
            }
            return copies;
        }
    }

    /// <summary>Places the entry for the aligned block of <paramref name="width"/> slots starting at <paramref name="slot"/>.</summary>
    /// <remarks>
    /// What covers the block is the deepest node stored above its position, because a node carrying a compressed prefix
    /// is always stored at its anchor and only prefixless interior branches are left implicit. That node either spans
    /// the block itself, holds the block's whole subtree as an inlined leaf, or names it through a link, whose hash
    /// composition reads back through <see cref="LinkHash"/>.
    /// </remarks>
    private static void ResolveBlock<TFrame>(ref TFrame reader, int bitDepth, uint stored, uint copies, int slot, int width, ref Frontier frontier)
        where TFrame : struct, IGroupFrame<TKey, TPath>
    {
        int level = PbtFourLevelGroupGeometry.LevelsPerGroup - BitOperations.Log2((uint)width);
        int position = new NodeGroupPath(slot, level).Position;
        int depth = bitDepth + level;
        int covering = DeepestStoredAbove(stored, position);
        SpineNode node = NodeAt(ref reader, frontier.Root, bitDepth, covering);

        int branchDepth = node.BranchDepth;
        for (int bit = Math.Max(node.AnchorDepth, bitDepth); bit < Math.Min(branchDepth, depth); bit++)
            if (GetBit(node.Prefix.Bytes, bit - node.AnchorDepth) != SlotBit(slot, bitDepth, bit)) return;

        if (branchDepth >= depth)
        {
            // The covering node is the only subtree under this block, so it settles where its own branch reaches.
            int entryLevel = Math.Min(branchDepth, bitDepth + PbtFourLevelGroupGeometry.LevelsPerGroup) - bitDepth;
            int entrySlot = 0;
            // Above its anchor the covering node's path is this block's own, since it is stored on the block's chain.
            for (int bit = bitDepth; bit < bitDepth + entryLevel; bit++)
                entrySlot = (entrySlot << 1) | (bit < node.AnchorDepth ? SlotBit(slot, bitDepth, bit) : GetBit(node.Prefix.Bytes, bit - node.AnchorDepth));
            entrySlot <<= PbtFourLevelGroupGeometry.LevelsPerGroup - entryLevel;
            frontier.Place(entrySlot, new NodeGroupPath(entrySlot, entryLevel).Position, EntrySource.AtPosition,
                covering < 0 ? RootSource : covering);
            return;
        }

        int side = SlotBit(slot, bitDepth, branchDepth);
        PbtNodeReader branch = node.Node;
        ReadOnlySpan<byte> leafKey = side == 0 ? branch.LeftKey : branch.RightKey;
        if (!leafKey.IsEmpty)
        {
            // An inlined leaf is the whole subtree under its link, which is wider than this block when the link is
            // shallower than it; the block that holds the leaf's own slot claims it, the others hold nothing.
            int leafSlot = BoundarySlot(leafKey, bitDepth);
            if ((uint)(leafSlot - slot) >= (uint)width) return;
            frontier.Place(leafSlot, BoundaryPosition(leafSlot), side == 0 ? EntrySource.LeftLeafOf : EntrySource.RightLeafOf,
                covering < 0 ? RootSource : covering);
            return;
        }

        // The link names a node, so every level between it and this block holds a prefixless branch left implicit,
        // and this block's position holds a node of its own.
        int linkLevel = branchDepth + 1 - bitDepth;
        int linkPosition = new NodeGroupPath(slot & ~((PbtFourLevelGroupGeometry.BoundarySlots >> linkLevel) - 1), linkLevel).Position;
        Debug.Assert(linkPosition == position || level < PbtFourLevelGroupGeometry.LevelsPerGroup,
            "A boundary node is never left implicit, so its link addresses it directly.");
        if ((copies & (1u << position)) == 0) frontier.Place(slot, position, EntrySource.AtPosition, position);
    }

    /// <summary>The hash the parent's link holds for the untouched node at <paramref name="position"/>, or default when no link names it.</summary>
    /// <remarks>
    /// The parent is read back from the group and input <see cref="Decompose"/> resolved against, which composition
    /// leaves unchanged. A level left implicit carries no link, so a node below one keeps the single hash its own
    /// encoding needs, which is computed once on demand. Nothing is hashed here.
    /// </remarks>
    private static ValueHash256 LinkHash<TFrame>(scoped ref TFrame reader, scoped in Frontier frontier, int position)
        where TFrame : struct, IGroupFrame<TKey, TPath> =>
        LinkParentAt(ref reader, frontier, DeepestStoredAbove(frontier.Stored, position)).HashOf(position);

    /// <summary>The links of the node the group stores at <paramref name="position"/>, or of its input for -1.</summary>
    /// <remarks>An input that is empty or a leaf holds no link.</remarks>
    private static LinkParent LinkParentAt<TFrame>(scoped ref TFrame reader, scoped in Frontier frontier, int position)
        where TFrame : struct, IGroupFrame<TKey, TPath>
    {
        if (position < 0 && (frontier.Root.IsEmpty || frontier.Root.IsLeaf)) return default;
        int bitDepth = reader.BitDepth;
        SpineNode node = NodeAt(ref reader, frontier.Root, bitDepth, position);
        return new(node.BranchDepth + 1 - bitDepth, node.Node.LeftHash, node.Node.RightHash);
    }

    /// <summary>The links of the deepest node a group stores above a position, read once for every untouched node they name.</summary>
    /// <param name="linkLength">The local path length of the positions the links name, or zero for none, as no link names the group root.</param>
    internal readonly struct LinkParent(int linkLength, in ValueHash256 leftHash, in ValueHash256 rightHash)
    {
        private readonly int _linkLength = linkLength;
        private readonly ValueHash256 _leftHash = leftHash;
        private readonly ValueHash256 _rightHash = rightHash;

        /// <summary>The hash the link naming <paramref name="position"/> holds, or default when no link names it.</summary>
        internal ValueHash256 HashOf(int position)
        {
            NodeGroupPath local = PbtFourLevelGroupGeometry.LocalPathOf(position);
            if (local.Length != _linkLength) return default;
            return local.GetBit(local.Length - 1) == 0 ? _leftHash : _rightHash;
        }
    }

    /// <summary>The deepest position above <paramref name="position"/> that the group stores a node at, or -1 for its root.</summary>
    private static int DeepestStoredAbove(uint stored, int position)
    {
        int ancestor = PbtFourLevelGroupGeometry.ParentOf(position);
        while (ancestor >= 0 && (stored & (1u << ancestor)) == 0) ancestor = PbtFourLevelGroupGeometry.ParentOf(ancestor);
        return ancestor;
    }

    /// <summary>The bit at <paramref name="bit"/> of the path to a boundary slot, which only spans this group.</summary>
    private static int SlotBit(int slot, int bitDepth, int bit) =>
        (slot >> (bitDepth + PbtFourLevelGroupGeometry.LevelsPerGroup - 1 - bit)) & 1;

    /// <summary>A node the decomposition reads for its links: one the group stores, or the group's own root.</summary>
    private readonly ref struct SpineNode(PbtNodeReader node, int anchorDepth)
    {
        internal readonly PbtNodeReader Node = node;
        /// <summary>The absolute depth this node's compressed prefix starts at, above the group for a spanning input.</summary>
        internal readonly int AnchorDepth = anchorDepth;
        internal CompressedPrefix Prefix => Node.Prefix;
        internal int BranchDepth => AnchorDepth + Node.Prefix.BitCount;
    }

    private static SpineNode NodeAt<TFrame>(ref TFrame reader, scoped in BoundaryNode root, int bitDepth, int position) where TFrame : struct, IGroupFrame<TKey, TPath> =>
        position < 0
            ? new SpineNode(root.Reader, root.AnchorDepth)
            : new SpineNode(PbtNodeReader.FromValidated(reader.GetEncoding(position).Span),
                bitDepth + PbtFourLevelGroupGeometry.LocalPathOf(position).Length);

    internal static int MatchingPrefixBits(CompressedPrefix prefix, TKey key, int keyOffset)
    {
        if (prefix.BitCount == 0) return 0;

        ReadOnlySpan<byte> prefixBytes = prefix.Bytes;
        int available = key.BitLength - keyOffset;
        int count = Math.Min(prefix.BitCount, available);
        ReadOnlySpan<byte> keyBytes = key.Bytes;
        int keyBitOffset = keyOffset & 7;
        if (keyBitOffset == 0)
            return Math.Min(count, PbtKeyOperations.FirstDifferingBit(prefixBytes, keyBytes[(keyOffset >> 3)..], 0));

        int index = 0;
        for (; index + 64 <= count; index += 64)
        {
            int keyByteIndex = (keyOffset + index) >> 3;
            ulong keyWord = (BinaryPrimitives.ReadUInt64BigEndian(keyBytes[keyByteIndex..]) << keyBitOffset)
                | (uint)(keyBytes[keyByteIndex + sizeof(ulong)] >> (8 - keyBitOffset));
            ulong difference = BinaryPrimitives.ReadUInt64BigEndian(prefixBytes[(index >> 3)..]) ^ keyWord;
            if (difference != 0) return index + BitOperations.LeadingZeroCount(difference);
        }
        for (; index + 8 <= count; index += 8)
        {
            int keyByteIndex = (keyOffset + index) >> 3;
            int keyByte = ((keyBytes[keyByteIndex] << 8) | keyBytes[keyByteIndex + 1]) >> (8 - keyBitOffset);
            int difference = prefixBytes[index >> 3] ^ (keyByte & 0xFF);
            if (difference != 0) return index + BitOperations.LeadingZeroCount((uint)difference) - 24;
        }
        while (index < count && GetBit(prefixBytes, index) == key.GetBit(keyOffset + index)) index++;
        return index;
    }
}
