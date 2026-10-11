// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using static Nethermind.Pbt.TrieUpdater;

namespace Nethermind.Pbt;

public static partial class TrieUpdater<TKey, TPath>
    where TKey : unmanaged, IPbtKey<TKey>
    where TPath : struct, IPbtNodePath<TPath>
{
    [Conditional("DEBUG")]
    public static void AssertSorted(ReadOnlySpan<PbtWriteOperation<TKey>> operations)
    {
        for (int index = 1; index < operations.Length; index++)
            Debug.Assert(operations[index - 1].Key.CompareTo(operations[index].Key) < 0, "Operations must be in strictly ascending key order.");
    }

    /// <summary>The longest node encoding: a branch whose prefix and both inlined keys are as long as a key can be.</summary>
    private const int MaxNodeLength = PbtNodeCodec.MaxBranchPreimageLength + PbtNodeCodec.BranchTrailerHeaderLength + 2 * PbtVariableTreeKey.MaxLength;

    /// <summary>A node a fold encoded for its caller to place, anchored at the depth the caller places it at.</summary>
    public struct SlotNode(int length, in ValueHash256 hash)
    {
        /// <summary>The encoding's length, or zero when the subtree folded away.</summary>
        public readonly int Length = length;
        /// <summary>The node's hash, or default when its encoding still has to be hashed.</summary>
        public readonly ValueHash256 Hash = hash;
        /// <summary>The change in stored size across the groups this node was folded from, still owed to the caller's boundary slot.</summary>
        public long SizeDelta;
        public readonly bool IsEmpty => Length == 0;
    }

    /// <summary>Consumes a subtree and applies its sorted mutation range, encoding the canonical replacement.</summary>
    /// <remarks>
    /// Mutations sharing a prefix share traversal through four-bit groups (16 boundary slots). Shared prefixes skip
    /// intermediate groups; each group is rebuilt by the in-frame recursion, which folds its touched slots below.
    /// </remarks>
    /// <param name="bitDepth">The depth the caller places the result at; the range and <paramref name="input"/> share the path down to it.</param>
    /// <param name="encoding">Receives the result's encoding, at least <see cref="MaxNodeLength"/> bytes.</param>
    [SkipLocalsInit]
    private static SlotNode FoldSortedRange<TFrame>(FoldContext context, ref TFrame ownerReader, scoped in BoundaryNode input,
        ReadOnlySpan<PbtWriteOperation<TKey>> operations, ref PbtTraversalPath path, int bitDepth, scoped Span<byte> encoding)
        where TFrame : struct, IGroupFrame<TKey, TPath>
    {
        Debug.Assert(path.BitDepth == bitDepth);
        BoundaryNode current = input;
        if (operations.IsEmpty) return EncodeReanchored(current, path, bitDepth, encoding);

        if (operations.Length == 1)
        {
            PbtWriteOperation<TKey> operation = operations[0];
            if (current.IsEmpty)
                return operation.Value == default ? default : EncodeLeaf(operation.Key, operation.Value, encoding);
            if (current.IsLeaf)
            {
                TKey leafKey = current.LeafKey(path);
                if (operation.Key.Equals(leafKey))
                {
                    if (operation.Value == default) return default;
                    return EncodeLeaf(leafKey, operation.Value, encoding);
                }
                if (operation.Value == default) return EncodeLeaf(leafKey, current.Hash, encoding);
                int divergenceDepth = leafKey.FirstDifferingBit(operation.Key, bitDepth);
                if (divergenceDepth < Math.Min(leafKey.BitLength, operation.Key.BitLength))
                    return EncodeTwoLeafBranch(leafKey, current.Hash, operation.Key, operation.Value, divergenceDepth, bitDepth, encoding);
            }
            else if (current.LeafChildrenMask == (LeftLeaf | RightLeaf))
            {
                bool right = operation.Key.Equals(current.RightLeafKey(path));
                if (right || operation.Key.Equals(current.LeftLeafKey(path)))
                {
                    if (operation.Value == default)
                        return right ? EncodeLeaf(current.LeftLeafKey(path), current.LeftHash, encoding) : EncodeLeaf(current.RightLeafKey(path), current.RightHash, encoding);
                    ValueHash256 leafHash = operation.Value;
                    if (leafHash == (right ? current.RightHash : current.LeftHash)) return EncodeReanchored(current, path, bitDepth, encoding);
                    PbtBranchReader branch = current.Reader;
                    int length = PbtNodeCodec.EncodeReanchored(branch, current.AnchorDepth, bitDepth, right ? branch.LeftHash : leafHash, right ? leafHash : branch.RightHash, encoding);
                    return new SlotNode(length, default);
                }
                if (operation.Value == default) return EncodeReanchored(current, path, bitDepth, encoding);
            }
        }

        TKey firstKey = operations[0].Key;
        int branchDepth = operations.Length == 1 ? firstKey.BitLength : firstKey.FirstDifferingBit(operations[^1].Key, bitDepth);
        if (current.IsLeaf)
            branchDepth = Math.Min(branchDepth, current.LeafKey(path).FirstDifferingBit(firstKey, bitDepth));
        else if (!current.IsEmpty)
            branchDepth = Math.Min(branchDepth, current.FirstDifferingBit(path, firstKey, bitDepth));
        int groupDepth = branchDepth / PbtFourLevelGroupGeometry.LevelsPerGroup * PbtFourLevelGroupGeometry.LevelsPerGroup;
        if (groupDepth > bitDepth)
        {
            path.AppendKey(firstKey.Bytes, groupDepth);
            SlotNode result = FoldSortedInOwnFrame(context, ref ownerReader, current, operations, ref path, groupDepth, bitDepth, encoding);
            path.Truncate(bitDepth);
            return result;
        }

        return FoldSortedInOwnFrame(context, ref ownerReader, current, operations, ref path, bitDepth, bitDepth, encoding);
    }

    /// <summary>Folds a group deeper than the open frame in a frame of its own, opening the group absent or stored.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    [SkipLocalsInit]
    private static SlotNode FoldSortedInOwnFrame<TFrame>(FoldContext context, ref TFrame ownerReader, scoped in BoundaryNode current,
        ReadOnlySpan<PbtWriteOperation<TKey>> operations, ref PbtTraversalPath path, int bitDepth, int anchorDepth, scoped Span<byte> encoding)
        where TFrame : struct, IGroupFrame<TKey, TPath>
    {
        AbsentGroupFrame<TKey, TPath> absent;
        // A spanning branch may inline two leaves as well, so it is checked first: it carries down the size the owner holds
        // of everything below the boundary slot on the way here, which keeps the owner's accounting.
        if (IsAbsentGroupBelow(current, bitDepth))
            absent = new(bitDepth, current.BranchSlot(path, bitDepth), ownerReader.DescendantBytes(BoundarySlot(path.Bytes, ownerReader.BitDepth)));
        else if (OwnsNoGroup(current))
            absent = new(bitDepth);
        else
        {
            GroupFrameReader<TKey, TPath> reader = new(context.Store, path, current.HashAt(bitDepth));
            using (new GroupFrameReader<TKey, TPath>.Scope(ref reader))
                return FoldSortedAndPublish(context, ref reader, current, operations, ref path, bitDepth, anchorDepth, encoding);
        }
        return FoldSortedAndPublish(context, ref absent, current, operations, ref path, bitDepth, anchorDepth, encoding);
    }

    /// <summary>Folds the group of <paramref name="reader"/> and publishes it, detaching its root as the encoding its caller places.</summary>
    [SkipLocalsInit]
    private static SlotNode FoldSortedAndPublish<TFrame>(FoldContext context, ref TFrame reader, scoped in BoundaryNode current,
        ReadOnlySpan<PbtWriteOperation<TKey>> operations, ref PbtTraversalPath path, int bitDepth, int anchorDepth, scoped Span<byte> encoding)
        where TFrame : struct, IGroupFrame<TKey, TPath>
    {
        using PbtNodeGroupWriter<TPath> writer = PbtNodeGroupWriter<TPath>.Rent(bitDepth, context.MemoryProvider);
        StoredGroupHashes.Open(out StoredGroupHashes hashes);
        ComposedNode root = WalkFrame(context, ref reader, ref hashes, writer, current, operations, path);
        SlotNode result = default;
        ValueHash256 groupHash = default;
        if (!root.IsEmpty)
        {
            ReadOnlySpan<byte> node = writer.Entry(root.Offset, root.Length).Span;
            groupHash = root.Hash != default ? root.Hash : HashBranch(node);
            result = anchorDepth == bitDepth || PbtNodeCodec.IsLeaf(node)
                ? Copy(node, groupHash, encoding)
                : new SlotNode(EncodeLifted(PbtBranchReader.FromValidated(node), path, anchorDepth, encoding), default);
            writer.DropLast(PbtFourLevelGroupGeometry.RootPosition);
        }
        result.SizeDelta = PublishGroup(context.Writer, ref reader, writer, path, groupHash);
        return result;
    }

    /// <summary>Rebuilds the open frame's group from <paramref name="input"/> and the range, leaving its root as the last entry, at the root position.</summary>
    private static ComposedNode WalkFrame<TFrame>(FoldContext context, ref TFrame reader, ref StoredGroupHashes hashes, PbtNodeGroupWriter<TPath> writer,
        scoped in BoundaryNode input, ReadOnlySpan<PbtWriteOperation<TKey>> operations, PbtTraversalPath path)
        where TFrame : struct, IGroupFrame<TKey, TPath>
    {
        writer.ReserveFirstBuffer(reader.PayloadLength);
        SortedWalk<TFrame> walk = new(context, ref reader, ref hashes, writer, path, input, operations);
        try
        {
            TryFoldSlotsInParallel(ref walk);
            return WalkRoot(ref walk, input.IsEmpty);
        }
        finally
        {
            if (walk.FoldedAhead is { } foldedAhead) ArrayPool<byte>.Shared.Return(foldedAhead);
            if (walk.FoldedAheadNodes is { } foldedAheadNodes) ArrayPool<SlotNode>.Shared.Return(foldedAheadNodes);
        }
    }

    /// <summary>Folds the groups below the frame's touched boundary slots across threads, leaving each result folded ahead for the walk.</summary>
    /// <remarks>
    /// A slot is worth a worker when its cover is a branch, which owns a group below it, or when it holds two or more
    /// operations, which build one. A single operation over an empty or leaf cover is trivial, and lies beneath the
    /// walk's single-operation shortcut, which would skip its result. Every boundary node and old hash is resolved
    /// here, on the calling thread, since the frame's hash cache is not shared. Each worker writes its slot's encoding
    /// into a slice of its own and never touches the frame, whose group lease outlives the loop, so boundary nodes are
    /// read from it in place. Slots fold on the calling thread while the quota has no free slot, as
    /// <see cref="TrieUpdater.ForEachOnQuota"/> does.
    /// </remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    [SkipLocalsInit]
    private static void TryFoldSlotsInParallel<TFrame>(scoped ref SortedWalk<TFrame> walk)
        where TFrame : struct, IGroupFrame<TKey, TPath>
    {
        FoldContext context = walk.Context;
        FoldFanOut fanOut = context.FanOut;
        ReadOnlySpan<PbtWriteOperation<TKey>> operations = walk.Operations;
        if (operations.Length < 2 * Math.Min(fanOut.MinOperationsPerWorker, fanOut.LargeSubtreeMinOperationsPerWorker)) return;

        Span<int> slots = stackalloc int[PbtFourLevelGroupGeometry.BoundarySlots];
        Span<int> starts = stackalloc int[PbtFourLevelGroupGeometry.BoundarySlots];
        Span<int> counts = stackalloc int[PbtFourLevelGroupGeometry.BoundarySlots];
        Span<long> descendantBytes = stackalloc long[PbtFourLevelGroupGeometry.BoundarySlots];
        Span<Cover> covers = stackalloc Cover[PbtFourLevelGroupGeometry.BoundarySlots];
        int bitDepth = walk.BitDepth;
        int foldCount = 0;
        for (int index = 0; index < operations.Length;)
        {
            int slot = walk.SlotAt(index);
            int start = index;
            index = walk.SlotEnd(start, slot);
            Cover cover = walk.CoverAt(slot);
            if (index - start == 1 && (cover.IsEmpty || walk.IsLeaf(cover))) continue;
            slots[foldCount] = slot;
            starts[foldCount] = start;
            counts[foldCount] = index - start;
            descendantBytes[foldCount] = walk.Reader.DescendantBytes(slot);
            covers[foldCount++] = cover;
        }

        Span<int> runEnds = stackalloc int[PbtFourLevelGroupGeometry.BoundarySlots];
        int runCount = fanOut.PlanBucketRuns(counts[..foldCount], descendantBytes[..foldCount], runEnds);
        if (runCount < 2) return;

        // A touched boundary sibling needs its old hash to key its group, and an untouched one its hash for the branch
        // over them, so both are hashed together.
        for (int fold = 0; fold < foldCount; fold++)
        {
            int slot = slots[fold];
            if ((slot & 1) != 0 && fold > 0 && slots[fold - 1] == slot - 1) continue;
            if (covers[fold].Kind != CoverKind.Stored || walk.CoverAt(slot ^ 1).Kind != CoverKind.Stored) continue;
            int left = slot & ~1;
            walk.Hashes.GetChildHashesPaired(ref walk.Reader, PbtFourLevelGroupGeometry.BoundaryPosition(left), PbtFourLevelGroupGeometry.BoundaryPosition(left + 1), out _, out _);
        }

        int operationOffset = OffsetOf(context.Operations!, operations);
        SortedSlotFold[] folds = ArrayPool<SortedSlotFold>.Shared.Rent(foldCount);
        for (int fold = 0; fold < foldCount; fold++)
        {
            folds[fold] = new SortedSlotFold(slots[fold], operationOffset + starts[fold], counts[fold], walk.Boundary(covers[fold]), descendantBytes[fold]);
        }
        walk.FoldedAhead ??= ArrayPool<byte>.Shared.Rent(PbtFourLevelGroupGeometry.BoundarySlots * MaxNodeLength);
        walk.FoldedAheadNodes ??= ArrayPool<SlotNode>.Shared.Rent(PbtFourLevelGroupGeometry.BoundarySlots);
        FoldSlotRuns(context, folds, runEnds[..runCount], walk.Path.ToPath<TPath>(), bitDepth, walk.FoldedAhead);

        foreach (ref SortedSlotFold fold in folds.AsSpan(0, foldCount))
        {
            walk.Writer.AddDescendantDelta(fold.Slot, fold.Result.SizeDelta);
            walk.FoldedAheadNodes[fold.Slot] = fold.Result;
            walk.FoldedAheadMask |= 1 << fold.Slot;
        }
        // The folds hold boundary encodings; clear so the pool does not keep them alive.
        ArrayPool<SortedSlotFold>.Shared.Return(folds, clearArray: true);
    }

    /// <summary>Folds the planned runs of slots, the part of <see cref="TryFoldSlotsInParallel"/> past its early exits.</summary>
    /// <remarks>Kept apart so the closure over the runs is allocated only once a frame is known to split.</remarks>
    private static void FoldSlotRuns(FoldContext context, SortedSlotFold[] folds, scoped ReadOnlySpan<int> runEnds, TPath groupPath, int bitDepth, byte[] foldedAhead)
    {
        using ArrayPoolList<int> runs = new(runEnds);
        ForEachOnQuota(context.FoldQuota, runs.Count, run =>
        {
            for (int fold = run == 0 ? 0 : runs[run - 1]; fold < runs[run]; fold++)
                folds[fold].Fold(context, groupPath, bitDepth, foldedAhead);
        });
    }

    /// <summary>Sorts a zone's operations, which the producer grouped by <paramref name="shardTable"/>'s shards, by sorting each shard in place, and replaces every set value with its leaf hash.</summary>
    /// <remarks>
    /// The shards lie in ascending shard order and every key in one shard shares the shard nibble, so sorted shards make a
    /// sorted zone. A zone wide enough for two workers sorts and hashes its shards across threads under the fold quota,
    /// taking the leaf hashes off the fold's own path and batching them for <see cref="Blake3Hash.HashMany"/>.
    /// </remarks>
    /// <param name="shardTable">The used-shard mask, then each used shard's count.</param>
    public static void SortShards(FoldContext context, Span<PbtWriteOperation<TKey>> operations, ReadOnlySpan<int> shardTable)
    {
        int shardCount = BitOperations.PopCount((uint)shardTable[0]);
        if (shardCount < 2 || operations.Length < 2 * context.FanOut.MinOperationsPerWorker)
        {
            int offset = 0;
            foreach (int count in shardTable.Slice(1, shardCount))
            {
                PbtOperationSort.Sort(operations.Slice(offset, count));
                offset += count;
            }
            HashLeaves(operations);
            return;
        }

        PbtWriteOperation<TKey>[] array = context.Operations!;
        int[] starts = new int[shardCount + 1];
        starts[0] = OffsetOf(array, operations);
        for (int shard = 0; shard < shardCount; shard++) starts[shard + 1] = starts[shard] + shardTable[1 + shard];
        ForEachOnQuota(context.FoldQuota, shardCount, shard =>
        {
            Span<PbtWriteOperation<TKey>> shardOperations = array.AsSpan(starts[shard], starts[shard + 1] - starts[shard]);
            PbtOperationSort.Sort(shardOperations);
            HashLeaves(shardOperations);
        });
    }

    /// <summary>Folds the operations under boundary slot <paramref name="slot"/>, whose keys are of another type than the frame's, encoding the node the slot then holds.</summary>
    /// <param name="encoding">Receives the node the slot then holds, at least <see cref="MaxNodeLength"/> bytes.</param>
    public delegate SlotNode ForeignSlotFold(int slot, in BoundaryNode boundary, long descendantBytes, Span<byte> encoding);

    /// <summary>Rebuilds the open frame's group around its touched boundary slots, each folded by <paramref name="foldSlot"/>, leaving its root as the last entry, at the root position.</summary>
    /// <remarks>
    /// For a frame whose slots hold keys of other types than its own, so their operations cannot be walked here. Every
    /// touched slot's boundary node is resolved on the calling thread, then the slots fold across threads as
    /// <see cref="TrieUpdater.ForEachOnQuota"/> allows, and the walk places each result as one folded ahead. Two stand-in operations
    /// per touched slot drive the walk, so the single-operation shortcuts never apply; each sets a value exactly when its
    /// slot folded to a node, which is all the walk reads of them besides the slot their one-byte key names.
    /// </remarks>
    /// <param name="touchedSlots">The mask of the boundary slots <paramref name="foldSlot"/> folds.</param>
    public static ComposedNode WalkFrameOverForeignSlots<TFrame>(FoldContext context, ref TFrame reader, ref StoredGroupHashes hashes,
        PbtNodeGroupWriter<TPath> writer, in BoundaryNode input, PbtTraversalPath path, int touchedSlots, ForeignSlotFold foldSlot)
        where TFrame : struct, IGroupFrame<TKey, TPath>
    {
        Debug.Assert(path.BitDepth == 0, "Stand-in keys name a slot of the root group only.");
        writer.ReserveFirstBuffer(reader.PayloadLength);
        int slotCount = BitOperations.PopCount((uint)touchedSlots);
        using ArrayPoolList<PbtWriteOperation<TKey>> standIns = new(2 * slotCount);
        int[] slots = new int[slotCount];
        BoundaryNode[] boundaries = new BoundaryNode[slotCount];
        long[] descendantBytes = new long[slotCount];
        SortedWalk<TFrame> resolver = new(context, ref reader, ref hashes, writer, path, input, default);
        for (int remaining = touchedSlots, index = 0; remaining != 0; remaining &= remaining - 1, index++)
        {
            slots[index] = BitOperations.TrailingZeroCount(remaining);
            boundaries[index] = resolver.Boundary(resolver.CoverAt(slots[index]));
            descendantBytes[index] = reader.DescendantBytes(slots[index]);
        }

        byte[] foldedAhead = ArrayPool<byte>.Shared.Rent(PbtFourLevelGroupGeometry.BoundarySlots * MaxNodeLength);
        SlotNode[] foldedAheadNodes = ArrayPool<SlotNode>.Shared.Rent(PbtFourLevelGroupGeometry.BoundarySlots);
        try
        {
            ForEachOnQuota(context.FoldQuota, slotCount, index =>
                foldedAheadNodes[slots[index]] = foldSlot(slots[index], boundaries[index], descendantBytes[index],
                    foldedAhead.AsSpan(slots[index] * MaxNodeLength, MaxNodeLength)));
            foreach (int slot in slots)
            {
                writer.AddDescendantDelta(slot, foldedAheadNodes[slot].SizeDelta);
                PbtWriteOperation<TKey> standIn = new(TKey.Create([(byte)(slot << 4)]), foldedAheadNodes[slot].IsEmpty ? default : ValueKeccak.MaxValue);
                standIns.Add(standIn);
                standIns.Add(standIn);
            }

            SortedWalk<TFrame> walk = new(context, ref reader, ref hashes, writer, path, input, standIns.AsSpan())
            {
                FoldedAhead = foldedAhead,
                FoldedAheadNodes = foldedAheadNodes,
                FoldedAheadMask = touchedSlots,
            };
            return WalkRoot(ref walk, input.IsEmpty);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(foldedAhead);
            ArrayPool<SlotNode>.Shared.Return(foldedAheadNodes);
        }
    }

    /// <summary>Walks the open frame from its input, consuming every operation, and lands its root, the last entry, at the root position.</summary>
    private static ComposedNode WalkRoot<TFrame>(scoped ref SortedWalk<TFrame> walk, bool inputIsEmpty)
        where TFrame : struct, IGroupFrame<TKey, TPath>
    {
        ComposedNode root = Walk(ref walk, default, inputIsEmpty ? default : new Cover(CoverKind.Input, RootSource));
        Debug.Assert(walk.Next == walk.Operations.Length, "The walk consumes every operation of its frame.");
        return root.IsEmpty ? default : Land(ref walk.Reader, ref walk.Hashes, walk.Writer, walk.Path, root, PbtFourLevelGroupGeometry.RootPosition);
    }

    private static int OffsetOf(PbtWriteOperation<TKey>[] array, ReadOnlySpan<PbtWriteOperation<TKey>> span)
    {
        int offset = (int)(Unsafe.ByteOffset(ref MemoryMarshal.GetArrayDataReference(array), ref MemoryMarshal.GetReference(span)) / Unsafe.SizeOf<PbtWriteOperation<TKey>>());
        Debug.Assert(offset >= 0 && offset + span.Length <= array.Length, "The operation range must lie within the batch array.");
        return offset;
    }

    /// <summary>One boundary slot a frame folds on a worker, with the node it then holds.</summary>
    private struct SortedSlotFold(int slot, int offset, int count, BoundaryNode boundary, long descendantBytes)
    {
        public readonly int Slot = slot;
        public SlotNode Result;

        public void Fold(FoldContext context, TPath groupPath, int bitDepth, byte[] foldedAhead)
        {
            using IPbtConcurrentWriter writer = context.Store.CreateWriter();
            FoldContext workerContext = new(context.Store, writer, context.MemoryProvider, context.FoldQuota, context.Operations, context.FanOut);
            Result = FoldDetachedSlot(workerContext, groupPath, bitDepth, Slot, boundary, descendantBytes, context.Operations.AsSpan(offset, count),
                foldedAhead.AsSpan(Slot * MaxNodeLength, MaxNodeLength));
        }
    }

    /// <summary>Folds <paramref name="operations"/> below boundary slot <paramref name="slot"/> of the group at <paramref name="groupPath"/>, away from the frame that owns the slot.</summary>
    /// <remarks>
    /// The fold below only reads its owner's depth and this slot's descendant size, so a stand-in carrying that size
    /// replaces the owner frame, which stays with the calling thread.
    /// </remarks>
    /// <param name="encoding">Receives the node the slot then holds, at least <see cref="MaxNodeLength"/> bytes.</param>
    [SkipLocalsInit]
    public static SlotNode FoldDetachedSlot(FoldContext context, TPath groupPath, int bitDepth, int slot, in BoundaryNode boundary, long descendantBytes,
        ReadOnlySpan<PbtWriteOperation<TKey>> operations, Span<byte> encoding)
    {
        Span<byte> pathBuffer = stackalloc byte[PbtBitPrefix.ByteCount(TPath.MaxBitDepth)];
        PbtTraversalPath path = PbtTraversalPath.FromPath(pathBuffer, groupPath);
        path.AppendMut(slot);
        AbsentGroupFrame<TKey, TPath> owner = new(bitDepth, slot, descendantBytes);
        return FoldSortedRange(context, ref owner, boundary, operations, ref path, bitDepth + PbtFourLevelGroupGeometry.LevelsPerGroup, encoding);
    }

    private static SlotNode Copy(ReadOnlySpan<byte> node, in ValueHash256 hash, Span<byte> encoding)
    {
        node.CopyTo(encoding);
        return new SlotNode(node.Length, hash);
    }

    public static ValueHash256 HashBranch(ReadOnlySpan<byte> encoding) => Blake3Hash.Hash(PbtBranchReader.FromValidated(encoding).Preimage);

    /// <summary>How many leaves <see cref="HashLeaves"/> hashes in one <see cref="Blake3Hash.HashMany"/> call, the widest lane count.</summary>
    private const int LeafHashBatch = 16;

    /// <summary>Replaces every set operation's value with its leaf hash, hashing up to <see cref="LeafHashBatch"/> leaves at a time; deletions stay default.</summary>
    /// <remarks>A batch holds keys of one length only, so its preimages are equally long.</remarks>
    [SkipLocalsInit]
    private static void HashLeaves(Span<PbtWriteOperation<TKey>> operations)
    {
        Span<byte> preimages = stackalloc byte[LeafHashBatch * PbtNodeCodec.LeafPreimageLength(TKey.Capacity)];
        Span<int> indices = stackalloc int[LeafHashBatch];
        int count = 0;
        int preimageLength = 0;
        for (int index = 0; index < operations.Length; index++)
        {
            if (operations[index].Value == default) continue;
            TKey key = operations[index].Key;
            int length = PbtNodeCodec.LeafPreimageLength(key.Length);
            if (count == LeafHashBatch || (count > 0 && length != preimageLength))
            {
                HashLeafBatch(operations, indices[..count], preimages, preimageLength);
                count = 0;
            }
            preimageLength = length;
            PbtNodeCodec.WriteLeafPreimage(preimages.Slice(count * preimageLength, preimageLength), key.Bytes, operations[index].Value.Bytes);
            indices[count++] = index;
        }
        if (count > 0) HashLeafBatch(operations, indices[..count], preimages, preimageLength);
    }

    [SkipLocalsInit]
    private static void HashLeafBatch(Span<PbtWriteOperation<TKey>> operations, ReadOnlySpan<int> indices, ReadOnlySpan<byte> preimages, int preimageLength)
    {
        Span<ValueHash256> hashes = stackalloc ValueHash256[LeafHashBatch];
        Blake3Hash.HashMany(preimages[..(indices.Length * preimageLength)], preimageLength, hashes[..indices.Length]);
        for (int batchIndex = 0; batchIndex < indices.Length; batchIndex++)
        {
            int index = indices[batchIndex];
            operations[index] = new(operations[index].Key, hashes[batchIndex]);
        }
    }

    private static SlotNode EncodeLeaf(TKey key, in ValueHash256 hash, Span<byte> encoding)
    {
        PbtNodeCodec.EncodeLeaf(encoding, key);
        return new SlotNode(PbtNodeCodec.LeafLength(key.Length), hash);
    }

    /// <summary>Encodes <paramref name="node"/>, read against <paramref name="cursor"/>, anchored at <paramref name="anchorDepth"/>, at or below its own anchor.</summary>
    private static SlotNode EncodeReanchored(scoped in BoundaryNode node, scoped in PbtTraversalPath cursor, int anchorDepth, Span<byte> encoding)
    {
        if (node.IsEmpty) return default;
        if (node.IsLeaf) return EncodeLeaf(node.LeafKey(cursor), node.Hash, encoding);
        PbtBranchReader branch = node.Reader;
        return new SlotNode(PbtNodeCodec.EncodeReanchored(branch, node.AnchorDepth, anchorDepth, branch.LeftHash, branch.RightHash, encoding),
            anchorDepth == node.AnchorDepth ? node.Hash : default);
    }

    /// <summary>Encodes a group's root branch, stored at the group's depth, anchored higher at <paramref name="anchorDepth"/> after a prefix jump.</summary>
    /// <remarks>The bits between the two depths are the path's, which the jump skipped along the range's shared prefix.</remarks>
    private static int EncodeLifted(scoped PbtBranchReader root, scoped in PbtTraversalPath path, int anchorDepth, Span<byte> encoding)
    {
        CompressedPrefix prefix = root.Prefix;
        int liftedBits = path.BitDepth - anchorDepth;
        int bitCount = prefix.BitCount + liftedBits;
        // Lifted above its group, the root's inline keys take back the path bytes the shallower anchor no longer omits.
        int fromKeyOffset = PbtNodeCodec.InlineKeyOffset(path.BitDepth), toKeyOffset = PbtNodeCodec.InlineKeyOffset(anchorDepth);
        int length = PbtNodeCodec.BranchPreimageLength(bitCount) + PbtNodeCodec.BranchTrailerHeaderLength + PbtNodeCodec.RebasedKeysLength(root, fromKeyOffset, toKeyOffset);
        PbtNodeCodec.CreateBranchEncoding(encoding, bitCount, root.LeftHash, root.RightHash);
        PbtBitPrefix.CopyBits(path.Bytes, anchorDepth, liftedBits, encoding[3..], 0);
        if (prefix.BitCount != 0) PbtBitPrefix.CopyBits(prefix.Bytes, 0, prefix.BitCount, encoding[3..], liftedBits);
        PbtNodeCodec.WriteRebasedBranchTrailer(encoding[PbtNodeCodec.BranchPreimageLength(bitCount)..length], root, fromKeyOffset, toKeyOffset, path.Bytes);
        return length;
    }

    /// <summary>The branch over two leaves whose keys first differ at <paramref name="branchDepth"/>, anchored at <paramref name="anchorDepth"/>.</summary>
    private static SlotNode EncodeTwoLeafBranch(TKey first, in ValueHash256 firstHash, TKey second, in ValueHash256 secondHash, int branchDepth, int anchorDepth,
        Span<byte> encoding)
    {
        bool firstIsLeft = first.GetBit(branchDepth) == 0;
        TKey leftKey = firstIsLeft ? first : second;
        TKey rightKey = firstIsLeft ? second : first;
        int bitCount = branchDepth - anchorDepth;
        int keyOffset = PbtNodeCodec.InlineKeyOffset(anchorDepth);
        int length = PbtNodeCodec.BranchLength(bitCount, leftKey.Length - keyOffset, rightKey.Length - keyOffset);
        PbtNodeCodec.CreateBranchEncoding(encoding, bitCount, firstIsLeft ? firstHash : secondHash, firstIsLeft ? secondHash : firstHash);
        if (bitCount != 0) PbtBitPrefix.CopyBits(first.Bytes, anchorDepth, bitCount, encoding[3..], 0);
        PbtNodeCodec.WriteBranchTrailer(encoding[PbtNodeCodec.BranchPreimageLength(bitCount)..length], leftKey.Bytes[keyOffset..], rightKey.Bytes[keyOffset..]);
        return new SlotNode(length, default);
    }

    /// <summary>Composes the node at <paramref name="local"/> from its <paramref name="cover"/> and the operations below it, appending it to the group.</summary>
    /// <remarks>
    /// The returned node is the writer's last entry, or empty. The operations
    /// below <paramref name="local"/> are the ones at the walk's cursor sharing its path, which this call consumes.
    /// </remarks>
    [SkipLocalsInit]
    private static ComposedNode Walk<TFrame>(scoped ref SortedWalk<TFrame> walk, NodeGroupPath local, Cover cover)
        where TFrame : struct, IGroupFrame<TKey, TPath>
    {
        if (!walk.Owns(local)) return AppendUntouched(ref walk, local, cover);
        if (local.Length == PbtFourLevelGroupGeometry.LevelsPerGroup) return FoldSlot(ref walk, local, cover, walk.Take(local));
        if ((cover.IsEmpty || walk.IsLeaf(cover)) && !walk.OwnsFollowing(local)
            && TryWalkSingle(ref walk, local, cover, walk.Operations[walk.Next], out ComposedNode single))
        {
            walk.Advance(1);
            return single;
        }

        walk.ChildCovers(local, cover, out Cover leftCover, out Cover rightCover);
        int position = local.Position;
        // A touched boundary node needs its old hash to key the group below, and an untouched sibling its hash for the
        // branch over them, so both are hashed together.
        if (local.Length == PbtFourLevelGroupGeometry.LevelsPerGroup - 1 && leftCover.Kind == CoverKind.Stored && rightCover.Kind == CoverKind.Stored)
            walk.Hashes.GetChildHashesPaired(ref walk.Reader, position - local.Width, position - 1, out _, out _);
        ComposedNode left = Walk(ref walk, local.Left, leftCover);
        // Whatever the cursor still holds under this position lies on the right.
        bool rightIsEmpty = rightCover.IsEmpty
            ? !walk.HasSet(local.Right)
            : !left.IsEmpty && walk.Owns(local.Right) && !walk.HasSet(local.Right) && !Survives(ref walk, local.Right, rightCover, walk.Peek(local.Right));
        if (rightIsEmpty)
        {
            // Deletions that remove the whole right half, or find nothing there, leave no node to walk.
            walk.Advance(walk.CountOwned(local.Right));
            return left.IsEmpty ? default : left.Rise(position - local.Width, 0);
        }

        if (left.IsEmpty)
        {
            ComposedNode only = Walk(ref walk, local.Right, rightCover);
            return only.IsEmpty ? default : only.Rise(position - 1, 1);
        }

        ComposeFrame frame = default;
        int leftPosition = position - local.Width;
        // Only read back once SettleLeftSorted wrote it, so it is not zeroed.
        Unsafe.SkipInit(out OmittedPreimage omittedLeft);
        bool leftPending = SettleLeftSorted(walk.Writer, walk.Path, leftPosition, left.RiseBitCount == 0 ? left : Land(ref walk.Reader, ref walk.Hashes, walk.Writer, walk.Path, left, leftPosition), ref frame, omittedLeft);
        ComposedNode right = Walk(ref walk, local.Right, rightCover);
        Debug.Assert(!right.IsEmpty, "A right half known to survive composes a node.");
        return AppendBranchSorted(walk.Writer, walk.Path, position, right.RiseBitCount == 0 ? right : Land(ref walk.Reader, ref walk.Hashes, walk.Writer, walk.Path, right, position - 1), ref frame,
            leftPending ? (ReadOnlySpan<byte>)omittedLeft : default);
    }

    /// <summary>Settles the left child at <paramref name="leftPosition"/> into <paramref name="frame"/>, dropping it when the group does not keep it.</summary>
    /// <remarks>
    /// A leaf is inlined into the branch above it, so only its key and hash are kept. An omitted branch is dropped while it
    /// is still the last entry, since the right subtree is written over it, its preimage kept in <paramref name="omitted"/>
    /// for the caller to hash. A kept branch stays in the writer, its preimage read back from there.
    /// </remarks>
    /// <returns>Whether <paramref name="omitted"/> holds the left child's preimage, still to be hashed.</returns>
    private static bool SettleLeftSorted(PbtNodeGroupWriter<TPath> writer, scoped in PbtTraversalPath path, int leftPosition, in ComposedNode left, ref ComposeFrame frame,
        Span<byte> omitted)
    {
        ReadOnlySpan<byte> encoding = writer.Entry(left.Offset, left.Length).Span;
        frame.LeftIsLeaf = PbtNodeCodec.IsLeaf(encoding);
        frame.LeftHash = left.Hash;
        frame.LeftPreimageLength = 0;
        if (frame.LeftIsLeaf)
        {
            frame.LeftKey = TKey.Create(PbtNodeCodec.LeafKey(encoding));
            writer.DropLast(leftPosition);
            return false;
        }
        PbtBranchReader node = PbtBranchReader.FromValidated(encoding);
        if (PbtNodeGroupCodec.ShouldOmit(leftPosition, encoding))
        {
            bool pending = frame.LeftHash == default;
            if (pending) node.Preimage.CopyTo(omitted);
            writer.DropLast(leftPosition);
            return pending;
        }
        writer.ValidateEntry(path, leftPosition, encoding);
        if (frame.LeftHash == default)
        {
            frame.LeftPreimageOffset = left.Offset;
            frame.LeftPreimageLength = node.Preimage.Length;
        }
        return false;
    }

    /// <summary>Settles the right child, the last entry, and appends the branch over it and the frame's left child at <paramref name="position"/>.</summary>
    /// <param name="omittedLeft">The pending preimage of an omitted left child, or empty when the frame holds the left child's.</param>
    [SkipLocalsInit]
    private static ComposedNode AppendBranchSorted(PbtNodeGroupWriter<TPath> writer, scoped in PbtTraversalPath path, int position, in ComposedNode right, ref ComposeFrame frame,
        ReadOnlySpan<byte> omittedLeft)
    {
        int rightPosition = position - 1;
        ReadOnlySpan<byte> encoding = writer.Entry(right.Offset, right.Length).Span;
        bool rightIsLeaf = PbtNodeCodec.IsLeaf(encoding);
        TKey rightKey = rightIsLeaf ? TKey.Create(PbtNodeCodec.LeafKey(encoding)) : default;
        ValueHash256 rightHash = right.Hash;
        ReadOnlySpan<byte> rightPreimage = rightHash == default ? PbtBranchReader.FromValidated(encoding).Preimage : default;
        ReadOnlySpan<byte> leftPreimage = !omittedLeft.IsEmpty ? omittedLeft
            : frame.LeftPreimageLength == 0 ? default : writer.Entry(frame.LeftPreimageOffset, frame.LeftPreimageLength).Span;
        HashPendingPair(leftPreimage, ref frame.LeftHash, rightPreimage, ref rightHash);
        if (rightIsLeaf || PbtNodeGroupCodec.ShouldOmit(rightPosition, encoding))
            writer.DropLast(rightPosition);
        else
            writer.ValidateEntry(path, rightPosition, encoding);

        int keyOffset = writer.KeyOffsetAt(position);
        int leftKeyLength = frame.LeftIsLeaf ? frame.LeftKey.Length - keyOffset : 0;
        int rightKeyLength = rightIsLeaf ? rightKey.Length - keyOffset : 0;
        int offset = writer.WrittenCount;
        int length = PbtNodeCodec.BranchLength(0, leftKeyLength, rightKeyLength);
        Span<byte> branch = writer.Append(position, length);
        PbtNodeCodec.CreateBranchEncoding(branch, 0, frame.LeftHash, rightHash);
        Span<byte> trailer = branch[PbtNodeCodec.BranchPreimageLength(0)..];
        PbtNodeCodec.WriteBranchTrailer(trailer, leftKeyLength, rightKeyLength);
        if (frame.LeftIsLeaf) frame.LeftKey.Bytes[keyOffset..].CopyTo(trailer[PbtNodeCodec.BranchTrailerHeaderLength..]);
        if (rightIsLeaf) rightKey.Bytes[keyOffset..].CopyTo(trailer[(PbtNodeCodec.BranchTrailerHeaderLength + leftKeyLength)..]);
        return new(offset, length, default);
    }

    /// <summary>Appends the branch at <paramref name="position"/> that the group leaves implicit, rebuilt from the children it stores.</summary>
    /// <remarks>
    /// Only an interior prefixless branch is ever left out (PbtNodeGroupCodec.ShouldOmit), so a boundary
    /// position or the root with nothing stored, or a child missing, is a corrupt group. A hash already known for the
    /// position, such as the one its parent's link holds, is kept, so the branch is only hashed again if a sibling's
    /// deletion promotes it. With that hash known and the branch left out again, its child hashes are only needed by
    /// <see cref="Land"/>, so they are resolved there instead, sparing the rehash of every unchanged node below it.
    /// Otherwise the omitted levels below it are hashed pairwise.
    /// </remarks>
    private static ComposedNode AppendImplicitBranchSorted<TFrame>(scoped ref TFrame reader, scoped ref StoredGroupHashes hashes, PbtNodeGroupWriter<TPath> writer,
        int position)
        where TFrame : struct, IGroupFrame<TKey, TPath>
    {
        int width = PbtFourLevelGroupGeometry.WidthOf(position);
        if (width is > 1 and < PbtFourLevelGroupGeometry.BoundarySlots)
        {
            int offset = writer.WrittenCount;
            int length = PbtNodeCodec.BranchLength(0, 0, 0);
            Span<byte> branch = writer.Append(position, length);
            // A known hash only stands in for the child hashes, which omission does not look at.
            ValueHash256 left = hashes.KnownHash(position), right = left;
            bool seeded = left != default;
            if (!seeded) hashes.GetChildHashesPaired(ref reader, position - width, position - 1, out left, out right);
            if (left != default && right != default)
            {
                PbtNodeCodec.CreateBranchEncoding(branch, 0, left, right);
                PbtNodeCodec.WriteBranchTrailer(branch[PbtNodeCodec.BranchPreimageLength(0)..], 0, 0);
                return new(offset, length, seeded ? left : default) { ChildHashesPending = seeded };
            }
        }
        throw new InvalidDataException("A referenced PBT node is missing.");
    }

    /// <summary>The preimage of an omitted prefixless branch, which has no inline leaves.</summary>
    [InlineArray(3 + 2 * ValueHash256.MemorySize)]
    private struct OmittedPreimage
    {
        private byte _element;
    }

    /// <summary>Applies the one operation below an empty or leaf <paramref name="cover"/> when no second leaf results.</summary>
    /// <returns>False when the operation inserts a key beside the leaf, which the walk splits down to where the two diverge.</returns>
    private static bool TryWalkSingle<TFrame>(scoped ref SortedWalk<TFrame> walk, NodeGroupPath local, Cover cover, in PbtWriteOperation<TKey> operation, out ComposedNode node)
        where TFrame : struct, IGroupFrame<TKey, TPath>
    {
        node = default;
        if (cover.IsEmpty)
        {
            if (operation.Value != default) node = AppendLeaf(walk.Writer, local.Position, operation.Key, operation.Value);
            return true;
        }
        if (!operation.Key.Equals(walk.LeafKey(cover)))
        {
            if (operation.Value != default) return false;
            node = AppendUntouched(ref walk, local, cover);
            return true;
        }
        if (operation.Value == default) return true;
        ValueHash256 leafHash = operation.Value;
        node = leafHash == walk.LeafHash(cover) ? AppendUntouched(ref walk, local, cover) : AppendLeaf(walk.Writer, local.Position, operation.Key, leafHash);
        return true;
    }

    /// <summary>Appends the unchanged subtree under <paramref name="local"/>, the node its cover holds there with the descendants the group keeps.</summary>
    private static ComposedNode AppendUntouched<TFrame>(scoped ref SortedWalk<TFrame> walk, NodeGroupPath local, Cover cover)
        where TFrame : struct, IGroupFrame<TKey, TPath>
    {
        int position = local.Position;
        switch (cover.Kind)
        {
            case CoverKind.Empty:
                return default;
            case CoverKind.Stored when cover.Position == position:
                {
                    // A node stored at its own position is one contiguous range with its descendants, ending at the node.
                    walk.Reader.CopyRange(walk.Writer, position - 2 * local.Width + 2, position + 1);
                    int length = walk.Reader.GetEncoding(position).Length;
                    return new ComposedNode(walk.Writer.WrittenCount - length, length, walk.Hashes.KnownHash(position));
                }
            case CoverKind.Implicit:
                CopyDescendants(ref walk, local);
                return AppendImplicitBranchSorted(ref walk.Reader, ref walk.Hashes, walk.Writer, position);
        }

        if (walk.IsLeaf(cover)) return AppendLeaf(walk.Writer, position, walk.LeafKey(cover), walk.LeafHash(cover));

        // A branch anchored above this position, whose compressed prefix passes through it.
        PbtBranchReader node = walk.Node(cover, out int anchorDepth);
        int depth = walk.BitDepth + local.Length;
        if (anchorDepth + node.Prefix.BitCount < walk.BitDepth + PbtFourLevelGroupGeometry.LevelsPerGroup) CopyDescendants(ref walk, local);
        return AppendReanchoredSorted(walk.Writer, position, anchorDepth, depth, node);
    }

    private static void CopyDescendants<TFrame>(scoped ref SortedWalk<TFrame> walk, NodeGroupPath local)
        where TFrame : struct, IGroupFrame<TKey, TPath> =>
        walk.Reader.CopyRange(walk.Writer, local.Position - 2 * local.Width + 2, local.Position);

    /// <summary>Appends a stored branch anchored at <paramref name="storedAnchorDepth"/> at <paramref name="position"/>, at <paramref name="depth"/>, with the rest of its compressed prefix.</summary>
    private static ComposedNode AppendReanchoredSorted(PbtNodeGroupWriter<TPath> writer, int position, int storedAnchorDepth, int depth, scoped PbtBranchReader stored)
    {
        int offset = writer.WrittenCount;
        int length = PbtNodeCodec.ReanchoredLength(stored, storedAnchorDepth, depth);
        PbtNodeCodec.EncodeReanchored(stored, storedAnchorDepth, depth, stored.LeftHash, stored.RightHash, writer.Append(position, length));
        return new(offset, length, default);
    }

    /// <summary>Hashes the sibling preimages still pending, together when both are.</summary>
    private static void HashPendingPair(ReadOnlySpan<byte> leftPreimage, ref ValueHash256 leftHash, ReadOnlySpan<byte> rightPreimage, ref ValueHash256 rightHash)
    {
        if (leftPreimage.IsEmpty && rightPreimage.IsEmpty) return;
        if (leftPreimage.IsEmpty)
        {
            rightHash = Blake3Hash.Hash(rightPreimage);
        }
        else if (rightPreimage.IsEmpty)
        {
            leftHash = Blake3Hash.Hash(leftPreimage);
        }
        else
        {
            Blake3Hash.HashTwo(leftPreimage, rightPreimage, out leftHash, out rightHash);
        }
    }

    private static ComposedNode AppendLeaf(PbtNodeGroupWriter<TPath> writer, int position, TKey key, in ValueHash256 hash)
    {
        int offset = writer.WrittenCount;
        int length = PbtNodeCodec.LeafLength(key.Length);
        PbtNodeCodec.EncodeLeaf(writer.Append(position, length), key);
        return new ComposedNode(offset, length, hash);
    }

    /// <summary>Folds the boundary slot <paramref name="local"/> in the group below and appends the node it returns.</summary>
    [SkipLocalsInit]
    private static ComposedNode FoldSlot<TFrame>(scoped ref SortedWalk<TFrame> walk, NodeGroupPath local, Cover cover, ReadOnlySpan<PbtWriteOperation<TKey>> operations)
        where TFrame : struct, IGroupFrame<TKey, TPath>
    {
        int slot = local.Slot;
        Span<byte> encoding = stackalloc byte[MaxNodeLength];
        SlotNode result;
        scoped ReadOnlySpan<byte> node;
        if ((walk.FoldedAheadMask >> slot & 1) != 0)
        {
            walk.FoldedAheadMask &= ~(1 << slot);
            result = walk.FoldedAheadNodes![slot];
            node = walk.FoldedAhead.AsSpan(slot * MaxNodeLength, result.Length);
        }
        else
        {
            result = FoldSlotBelow(ref walk, local, cover, operations, encoding);
            node = encoding[..result.Length];
        }
        if (result.IsEmpty) return default;
        int offset = walk.Writer.WrittenCount;
        node.CopyTo(walk.Writer.Append(local.Position, node.Length));
        return new ComposedNode(offset, node.Length, result.Hash);
    }

    /// <summary>Folds the group below the boundary slot <paramref name="local"/>, encoding the node the slot then holds into <paramref name="encoding"/>.</summary>
    private static SlotNode FoldSlotBelow<TFrame>(scoped ref SortedWalk<TFrame> walk, NodeGroupPath local, Cover cover, ReadOnlySpan<PbtWriteOperation<TKey>> operations, scoped Span<byte> encoding)
        where TFrame : struct, IGroupFrame<TKey, TPath>
    {
        int slot = local.Slot;
        BoundaryNode boundary = walk.Boundary(cover);
        int bitDepth = walk.BitDepth;
        int slotDepth = bitDepth + PbtFourLevelGroupGeometry.LevelsPerGroup;
        PbtTraversalPath slotPath = walk.Path;
        slotPath.AppendMut(slot);
        SlotNode result = FoldSortedRange(walk.Context, ref walk.Reader, boundary, operations, ref slotPath, slotDepth, encoding);
        slotPath.Truncate(bitDepth);
        walk.Writer.AddDescendantDelta(slot, result.SizeDelta);
        return result;
    }

    /// <summary>Whether anything under <paramref name="local"/> survives deletions that are all <paramref name="operations"/> hold.</summary>
    /// <remarks>
    /// A sibling must be known to survive before its left neighbour is settled. The keys are
    /// matched against the cover down to the boundary slots, whose groups are folded ahead and kept for the walk.
    /// </remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool Survives<TFrame>(scoped ref SortedWalk<TFrame> walk, NodeGroupPath local, Cover cover, ReadOnlySpan<PbtWriteOperation<TKey>> operations)
        where TFrame : struct, IGroupFrame<TKey, TPath>
    {
        if (cover.IsEmpty) return false;
        if (operations.IsEmpty) return true;
        if (walk.IsLeaf(cover))
        {
            TKey leafKey = walk.LeafKey(cover);
            // A leaf's slot folded ahead already settled whether the leaf survives, whatever operations stood in for it.
            int leafSlot = BoundarySlot(leafKey.Bytes, walk.BitDepth);
            if ((walk.FoldedAheadMask >> leafSlot & 1) != 0) return !walk.FoldedAheadNodes![leafSlot].IsEmpty;
            return !Contains(operations, leafKey);
        }
        if (local.Length == PbtFourLevelGroupGeometry.LevelsPerGroup)
        {
            int slot = local.Slot;
            if ((walk.FoldedAheadMask >> slot & 1) != 0) return !walk.FoldedAheadNodes![slot].IsEmpty;
            walk.FoldedAhead ??= ArrayPool<byte>.Shared.Rent(PbtFourLevelGroupGeometry.BoundarySlots * MaxNodeLength);
            walk.FoldedAheadNodes ??= ArrayPool<SlotNode>.Shared.Rent(PbtFourLevelGroupGeometry.BoundarySlots);
            SlotNode result = FoldSlotBelow(ref walk, local, cover, operations, walk.FoldedAhead.AsSpan(slot * MaxNodeLength, MaxNodeLength));
            walk.FoldedAheadNodes![slot] = result;
            walk.FoldedAheadMask |= 1 << slot;
            return !result.IsEmpty;
        }

        int split = SortedWalk<TFrame>.CountOwned(operations, 0, local.Left, walk.BitDepth);
        walk.ChildCovers(local, cover, out Cover leftCover, out Cover rightCover);
        if ((split == 0 && !leftCover.IsEmpty) || (split == operations.Length && !rightCover.IsEmpty)) return true;
        return Survives(ref walk, local.Left, leftCover, operations[..split]) || Survives(ref walk, local.Right, rightCover, operations[split..]);
    }

    private static bool Contains(ReadOnlySpan<PbtWriteOperation<TKey>> operations, TKey key)
    {
        int low = 0;
        int high = operations.Length - 1;
        while (low <= high)
        {
            int middle = (low + high) >>> 1;
            int comparison = operations[middle].Key.CompareTo(key);
            if (comparison == 0) return true;
            if (comparison < 0) low = middle + 1;
            else high = middle - 1;
        }
        return false;
    }

    /// <summary>The source position standing for the frame's input, which no group position addresses.</summary>
    private const int RootSource = PbtFourLevelGroupGeometry.PositionCount;

    private enum CoverKind : byte
    {
        Empty,
        /// <summary>The frame's input node: its root branch or leaf.</summary>
        Input,
        /// <summary>The branch stored at <see cref="Cover.Position"/>, at or above the covered position.</summary>
        Stored,
        /// <summary>The prefixless branch the group leaves implicit at <see cref="Cover.Position"/>.</summary>
        Implicit,
        /// <summary>The leaf inlined by the branch at <see cref="Cover.Position"/>, or by the input for <see cref="RootSource"/>.</summary>
        InlineLeaf,
    }

    /// <summary>The existing node holding every stored key under a walk position: anchored at or above it, splitting at or below it.</summary>
    private readonly struct Cover(CoverKind kind, int position, bool right = false)
    {
        public readonly CoverKind Kind = kind;
        public readonly byte Position = (byte)position;
        public readonly bool Right = right;
        public bool IsEmpty => Kind == CoverKind.Empty;
    }

    /// <summary>The frame one in-frame recursion rebuilds, and the slot results it folded ahead of the walk.</summary>
    private ref struct SortedWalk<TFrame>
        where TFrame : struct, IGroupFrame<TKey, TPath>
    {
        public readonly FoldContext Context;
        public ref TFrame Reader;
        public ref StoredGroupHashes Hashes;
        public readonly PbtNodeGroupWriter<TPath> Writer;
        public readonly PbtTraversalPath Path;
        public readonly int BitDepth;
        private readonly BoundaryNode _input;
        private readonly uint _stored;
        /// <summary>The encodings of the slots folded ahead of the walk, <see cref="MaxNodeLength"/> bytes per slot.</summary>
        public byte[]? FoldedAhead;
        public SlotNode[]? FoldedAheadNodes;
        public int FoldedAheadMask;
        /// <summary>The frame's operations, in key order.</summary>
        public readonly ReadOnlySpan<PbtWriteOperation<TKey>> Operations;
        /// <summary>The index of the first operation no position has consumed yet.</summary>
        public int Next;
        /// <summary>The boundary slot of the operation at <see cref="Next"/>, or one past the last slot once every operation is consumed.</summary>
        /// <remarks>Read only when the cursor moves, so a position checks whether it owns the next operation without reading its key.</remarks>
        private int _nextSlot;
        /// <summary>The boundary slot of the operation after <see cref="Next"/>, or -1 until it is read.</summary>
        private int _followingSlot;

        /// <summary>Whether the operation at the cursor lies under <paramref name="local"/>.</summary>
        public readonly bool Owns(NodeGroupPath local) => local.Covers(_nextSlot);

        /// <summary>Whether the operation after the cursor lies under <paramref name="local"/> too.</summary>
        public bool OwnsFollowing(NodeGroupPath local)
        {
            if (_followingSlot < 0) _followingSlot = SlotAt(Next + 1);
            return local.Covers(_followingSlot);
        }

        /// <summary>How many operations from the cursor on lie under <paramref name="local"/>.</summary>
        public readonly int CountOwned(NodeGroupPath local) => CountOwned(Operations, Next, local, BitDepth);

        public static int CountOwned(ReadOnlySpan<PbtWriteOperation<TKey>> operations, int start, NodeGroupPath local, int bitDepth)
        {
            int end = start;
            while (end < operations.Length && local.Covers(BoundarySlot(operations[end].Key.Bytes, bitDepth))) end++;
            return end - start;
        }

        /// <summary>The operations at the cursor under <paramref name="local"/>, left unconsumed.</summary>
        public readonly ReadOnlySpan<PbtWriteOperation<TKey>> Peek(NodeGroupPath local) => Operations.Slice(Next, CountOwned(local));

        /// <summary>Consumes the operations at the cursor in the boundary slot <paramref name="local"/>.</summary>
        /// <remarks>Each key is read once, to find where the slot ends.</remarks>
        public ReadOnlySpan<PbtWriteOperation<TKey>> Take(NodeGroupPath local)
        {
            Debug.Assert(local.Length == PbtFourLevelGroupGeometry.LevelsPerGroup && _nextSlot == local.Slot, "Only a touched boundary slot takes its operations.");
            int start = Next;
            int end = SlotEnd(start, local.Slot);
            Next = end;
            _nextSlot = SlotAt(end);
            _followingSlot = -1;
            return Operations[start..end];
        }

        /// <summary>Moves the cursor past <paramref name="count"/> operations.</summary>
        public void Advance(int count)
        {
            Next += count;
            _nextSlot = SlotAt(Next);
            _followingSlot = -1;
        }

        /// <summary>Whether any operation at the cursor under <paramref name="local"/> sets a value rather than deleting one.</summary>
        public readonly bool HasSet(NodeGroupPath local)
        {
            for (int index = Next, slot = _nextSlot; local.Covers(slot); slot = SlotAt(++index))
                if (Operations[index].Value != default) return true;
            return false;
        }

        /// <summary>The boundary slot of the operation at <paramref name="index"/>.</summary>
        public readonly int SlotAt(int index) =>
            index >= Operations.Length ? PbtFourLevelGroupGeometry.BoundarySlots : BoundarySlot(Operations[index].Key.Bytes, BitDepth);

        /// <summary>The index past the operations from <paramref name="start"/> on in boundary slot <paramref name="slot"/>.</summary>
        public readonly int SlotEnd(int start, int slot)
        {
            int index = start + 1;
            while (index < Operations.Length && BoundarySlot(Operations[index].Key.Bytes, BitDepth) == slot) index++;
            return index;
        }

        public SortedWalk(FoldContext context, ref TFrame reader, ref StoredGroupHashes hashes, PbtNodeGroupWriter<TPath> writer,
            PbtTraversalPath path, in BoundaryNode input, ReadOnlySpan<PbtWriteOperation<TKey>> operations)
        {
            Operations = operations;
            Context = context;
            Reader = ref reader;
            Hashes = ref hashes;
            Writer = writer;
            Path = path;
            BitDepth = path.BitDepth;
            _nextSlot = SlotAt(0);
            _followingSlot = -1;
            _input = input;
            // Only a branch splitting inside the group has anything stored in it; the root position is the input itself.
            _stored = !input.IsEmpty && !input.IsLeaf && input.BranchDepth < BitDepth + PbtFourLevelGroupGeometry.LevelsPerGroup
                ? reader.StoredPositions & ~(1u << PbtFourLevelGroupGeometry.RootPosition)
                : 0;
        }

        public readonly bool IsLeaf(Cover cover) => cover.Kind == CoverKind.InlineLeaf || (cover.Kind == CoverKind.Input && _input.IsLeaf);

        public readonly TKey LeafKey(Cover cover)
        {
            if (cover.Kind == CoverKind.Input) return _input.LeafKey(Path);
            ReadOnlySpan<byte> keyPostfix = LeafKeyPostfix(cover, out int keyOffset);
            return PbtKeyOperations.CreateKey<TKey>(Path.Bytes[..keyOffset], keyPostfix);
        }

        public readonly ValueHash256 LeafHash(Cover cover)
        {
            if (cover.Kind == CoverKind.Input) return _input.Hash;
            PbtBranchReader parent = Parent(cover);
            return cover.Right ? parent.RightHash : parent.LeftHash;
        }

        /// <summary>The leaf's key past the <paramref name="keyOffset"/> bytes the branch inlining it omits.</summary>
        private readonly ReadOnlySpan<byte> LeafKeyPostfix(Cover cover, out int keyOffset)
        {
            if (cover.Kind == CoverKind.Input)
            {
                keyOffset = _input.KeyOffset;
                return _input.LeafKeyPostfix;
            }
            // A stored parent sits below the group root, so its inline keys omit the group path's whole bytes.
            keyOffset = cover.Position == RootSource ? _input.KeyOffset : BitDepth >> 3;
            PbtBranchReader parent = Parent(cover);
            return cover.Right ? parent.RightKeyPostfix : parent.LeftKeyPostfix;
        }

        private readonly PbtBranchReader Parent(Cover cover) => cover.Position == RootSource
            ? _input.Reader
            : PbtBranchReader.FromValidated(Reader.GetEncoding(cover.Position).Span);

        /// <summary>The branch <paramref name="cover"/> holds, with the absolute depth its compressed prefix starts at.</summary>
        public readonly PbtBranchReader Node(Cover cover, out int anchorDepth)
        {
            if (cover.Kind == CoverKind.Input)
            {
                anchorDepth = _input.AnchorDepth;
                return _input.Reader;
            }
            anchorDepth = BitDepth + PbtFourLevelGroupGeometry.LocalPathOf(cover.Position).Length;
            return PbtBranchReader.FromValidated(Reader.GetEncoding(cover.Position).Span);
        }

        /// <summary>The covers of <paramref name="local"/>'s two children.</summary>
        public void ChildCovers(NodeGroupPath local, Cover cover, out Cover left, out Cover right)
        {
            left = default;
            right = default;
            int depth = BitDepth + local.Length;
            switch (cover.Kind)
            {
                case CoverKind.Empty:
                    return;
                case CoverKind.Implicit:
                    left = ChildNode(local.Left, default);
                    right = ChildNode(local.Right, default);
                    return;
            }

            if (IsLeaf(cover))
            {
                if (GetBit(LeafKeyPostfix(cover, out int keyOffset), depth - (keyOffset << 3)) == 0) left = cover;
                else right = cover;
                return;
            }

            PbtBranchReader node = Node(cover, out int anchorDepth);
            CompressedPrefix prefix = node.Prefix;
            int branchDepth = anchorDepth + prefix.BitCount;
            if (branchDepth > depth)
            {
                if (GetBit(prefix.Bytes, depth - anchorDepth) == 0) left = cover;
                else right = cover;
                return;
            }

            Debug.Assert(branchDepth == depth, "A cover splits at or below the position it covers.");
            int source = cover.Kind == CoverKind.Input ? RootSource : cover.Position;
            left = node.LeftKeyPostfix.IsEmpty ? ChildNode(local.Left, node.LeftHash) : new Cover(CoverKind.InlineLeaf, source, right: false);
            right = node.RightKeyPostfix.IsEmpty ? ChildNode(local.Right, node.RightHash) : new Cover(CoverKind.InlineLeaf, source, right: true);
        }

        /// <summary>The node anchored at <paramref name="local"/>, named by a link whose hash is seeded when the parent's encoding holds it.</summary>
        private readonly Cover ChildNode(NodeGroupPath local, in ValueHash256 linkHash)
        {
            int position = local.Position;
            if (linkHash != default) Hashes.Seed(position, linkHash);
            if ((_stored & (1u << position)) != 0) return new Cover(CoverKind.Stored, position);
            if (local.Length < PbtFourLevelGroupGeometry.LevelsPerGroup) return new Cover(CoverKind.Implicit, position);
            throw new InvalidDataException("A referenced PBT node is missing.");
        }

        /// <summary>The cover of boundary slot <paramref name="slot"/>, descended from the frame's input.</summary>
        public Cover CoverAt(int slot)
        {
            Cover cover = _input.IsEmpty ? default : new Cover(CoverKind.Input, RootSource);
            NodeGroupPath local = default;
            for (int level = 0; level < PbtFourLevelGroupGeometry.LevelsPerGroup && !cover.IsEmpty; level++)
            {
                ChildCovers(local, cover, out Cover left, out Cover right);
                bool isRight = (slot >> (PbtFourLevelGroupGeometry.LevelsPerGroup - 1 - level) & 1) != 0;
                cover = isRight ? right : left;
                local = isRight ? local.Right : local.Left;
            }
            return cover;
        }

        /// <summary>The boundary node <paramref name="cover"/> holds at a boundary slot, which the fold below it consumes.</summary>
        public readonly BoundaryNode Boundary(Cover cover) => cover.Kind switch
        {
            CoverKind.Empty => default,
            CoverKind.Input => _input,
            CoverKind.Stored => BoundaryNode.StoredAt(ref Reader, cover.Position, Hashes.GetHash(ref Reader, cover.Position)),
            _ => cover.Position == RootSource ? _input.InlineLeaf(cover.Right) : BoundaryNode.InlineLeafAt(ref Reader, cover.Position, cover.Right),
        };
    }
}
