// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Threading;
using static Nethermind.Pbt.TrieUpdater;

namespace Nethermind.Pbt;

internal static partial class TrieUpdater<TKey, TPath>
    where TKey : unmanaged, IPbtKey<TKey>
    where TPath : struct, IPbtNodePath<TPath>
{
    [Conditional("DEBUG")]
    private static void AssertSorted(ReadOnlySpan<PbtWriteOperation<TKey>> operations)
    {
        for (int index = 1; index < operations.Length; index++)
            Debug.Assert(operations[index - 1].Key.CompareTo(operations[index].Key) < 0, "Operations must be in strictly ascending key order.");
    }

    /// <summary>The longest node encoding: a branch whose prefix and both inlined keys are as long as a key can be.</summary>
    private const int MaxNodeLength = PbtNodeCodec.MaxBranchPreimageLength + PbtNodeCodec.BranchTrailerHeaderLength + 2 * PbtStorageTreeKey.MaxLength;

    /// <summary>A node a fold encoded for its caller to place, anchored at the depth the caller places it at.</summary>
    private struct SlotNode(int length, in ValueHash256 hash)
    {
        /// <summary>The encoding's length, or zero when the subtree folded away.</summary>
        internal readonly int Length = length;
        /// <summary>The node's hash, or default when its encoding still has to be hashed.</summary>
        internal readonly ValueHash256 Hash = hash;
        /// <summary>The change in stored size across the groups this node was folded from, still owed to the caller's boundary slot.</summary>
        internal long SizeDelta;
        internal readonly bool IsEmpty => Length == 0;
    }

    /// <summary>Consumes a subtree and applies its sorted mutation range, encoding the canonical replacement.</summary>
    /// <remarks>
    /// Mutations sharing a prefix share traversal through four-bit groups (16 boundary slots). Shared prefixes skip
    /// intermediate groups; each group is rebuilt by the in-frame recursion, which folds its touched slots below.
    /// </remarks>
    /// <param name="anchorDepth">The depth the caller places the result at; the range and <paramref name="input"/> share the path down to <paramref name="bitDepth"/>.</param>
    /// <param name="encoding">Receives the result's encoding, at least <see cref="MaxNodeLength"/> bytes.</param>
    [SkipLocalsInit]
    private static SlotNode FoldSortedRange<TFrame>(FoldContext context, ref TFrame ownerReader, scoped in BoundaryNode input,
        ReadOnlySpan<PbtWriteOperation<TKey>> operations, ref PbtTraversalPath path, int bitDepth, int anchorDepth, scoped Span<byte> encoding)
        where TFrame : struct, IGroupFrame<TKey, TPath>
    {
        Debug.Assert(path.BitDepth == bitDepth);
        BoundaryNode current = input;
        if (operations.IsEmpty) return EncodeReanchored(current, anchorDepth, encoding);

        if (current.IsEmpty)
        {
            if (operations.Length == 1)
                return operations[0].Value == default ? default : EncodeLeaf(operations[0].Key, HashLeaf(operations[0]), encoding);
        }
        else if (current.IsLeaf)
        {
            if (operations.Length == 1)
            {
                PbtWriteOperation<TKey> operation = operations[0];
                TKey leafKey = current.LeafKey;
                if (operation.Key.Equals(leafKey))
                {
                    if (operation.Value == default) return default;
                    return EncodeLeaf(leafKey, HashLeaf(operation), encoding);
                }
                if (operation.Value == default) return EncodeLeaf(leafKey, current.Hash, encoding);
                int divergenceDepth = leafKey.FirstDifferingBit(operation.Key, bitDepth);
                if (divergenceDepth < Math.Min(leafKey.BitLength, operation.Key.BitLength))
                    return EncodeTwoLeafBranch(leafKey, current.Hash, operation.Key, HashLeaf(operation), divergenceDepth, anchorDepth, encoding);
            }
        }
        else if (operations.Length == 1 && current.LeafChildrenMask == (LeftLeaf | RightLeaf))
        {
            PbtWriteOperation<TKey> operation = operations[0];
            bool right = operation.Key.Equals(current.RightLeafKey);
            if (right || operation.Key.Equals(current.LeftLeafKey))
            {
                if (operation.Value == default)
                    return right ? EncodeLeaf(current.LeftLeafKey, current.LeftHash, encoding) : EncodeLeaf(current.RightLeafKey, current.RightHash, encoding);
                ValueHash256 leafHash = HashLeaf(operation);
                if (leafHash == (right ? current.RightHash : current.LeftHash)) return EncodeReanchored(current, anchorDepth, encoding);
                PbtNodeReader branch = current.Reader;
                int length = EncodeReanchored(branch, anchorDepth - current.AnchorDepth, right ? branch.LeftHash : leafHash, right ? leafHash : branch.RightHash, encoding);
                return new SlotNode(length, default);
            }
            if (operation.Value == default) return EncodeReanchored(current, anchorDepth, encoding);
        }

        TKey firstKey = operations[0].Key;
        int branchDepth = operations.Length == 1 ? firstKey.BitLength : firstKey.FirstDifferingBit(operations[^1].Key, bitDepth);
        if (current.IsLeaf)
            branchDepth = Math.Min(branchDepth, current.LeafKey.FirstDifferingBit(firstKey, bitDepth));
        else if (!current.IsEmpty)
            branchDepth = Math.Min(branchDepth, current.FirstDifferingBit(path, firstKey, bitDepth));
        int groupDepth = branchDepth / PbtFourLevelGroupGeometry.LevelsPerGroup * PbtFourLevelGroupGeometry.LevelsPerGroup;
        if (groupDepth > bitDepth)
        {
            path.AppendKey(firstKey.Bytes, groupDepth);
            SlotNode result = FoldSortedRange(context, ref ownerReader, current, operations, ref path, groupDepth, anchorDepth, encoding);
            path.Truncate(bitDepth);
            return result;
        }

        return FoldSortedInOwnFrame(context, ref ownerReader, current, operations, ref path, bitDepth, anchorDepth, encoding);
    }

    /// <summary>Folds a group deeper than the open frame in a frame of its own, opening the group absent or stored.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    [SkipLocalsInit]
    private static SlotNode FoldSortedInOwnFrame<TFrame>(FoldContext context, ref TFrame ownerReader, scoped in BoundaryNode current,
        ReadOnlySpan<PbtWriteOperation<TKey>> operations, ref PbtTraversalPath path, int bitDepth, int anchorDepth, scoped Span<byte> encoding)
        where TFrame : struct, IGroupFrame<TKey, TPath>
    {
        if (IsAbsentGroup(current, bitDepth))
        {
            // The owner holds the size of everything below the boundary slot on the way here, which a spanning branch carries down.
            AbsentGroupFrame<TKey, TPath> absent = AbsentFrame(current, path, bitDepth, ownerReader.DescendantBytes(BoundarySlot(path.Bytes, ownerReader.BitDepth)));
            return FoldSortedAndPublish(context, ref absent, current, operations, ref path, bitDepth, anchorDepth, encoding);
        }
        GroupFrameReader<TKey, TPath> reader = new(context.Store, path, HashAt(current, bitDepth));
        using (new GroupFrameReader<TKey, TPath>.Scope(ref reader))
            return FoldSortedAndPublish(context, ref reader, current, operations, ref path, bitDepth, anchorDepth, encoding);
    }

    /// <summary>Folds the group of <paramref name="reader"/> and publishes it, detaching its root as the encoding its caller places.</summary>
    [SkipLocalsInit]
    private static SlotNode FoldSortedAndPublish<TFrame>(FoldContext context, ref TFrame reader, scoped in BoundaryNode current,
        ReadOnlySpan<PbtWriteOperation<TKey>> operations, ref PbtTraversalPath path, int bitDepth, int anchorDepth, scoped Span<byte> encoding)
        where TFrame : struct, IGroupFrame<TKey, TPath>
    {
        using PbtNodeGroupWriter<TPath> writer = PbtNodeGroupWriter<TPath>.Rent(bitDepth, context.MemoryProvider, context.PrefixlessBranchOmission);
        StoredGroupHashes.Open(out StoredGroupHashes hashes);
        ComposedNode root = WalkFrame(context, ref reader, ref hashes, writer, current, operations, path, default);
        SlotNode result = default;
        ValueHash256 groupHash = default;
        if (!root.IsEmpty)
        {
            ReadOnlySpan<byte> node = writer.Entry(root.Offset, root.Length).Span;
            groupHash = root.Hash != default ? root.Hash : HashBranch(node);
            result = anchorDepth == bitDepth || PbtNodeReader.FromValidated(node).IsLeaf
                ? Copy(node, groupHash, encoding)
                : new SlotNode(EncodeLifted(PbtNodeReader.FromValidated(node), path, anchorDepth, encoding), default);
            writer.DropLast(PbtFourLevelGroupGeometry.RootPosition);
        }
        result.SizeDelta = PublishGroup(context.Writer, ref reader, writer, path, groupHash);
        return result;
    }

    /// <summary>Rebuilds the open frame's group from <paramref name="input"/> and the range, leaving its root as the last entry, at the root position.</summary>
    /// <param name="shardTable">
    /// The producer's shard table when the operations are grouped by this frame's slot nibble: the used-slot mask, then
    /// each used slot's count. Slot ranges are then read from it rather than from the keys. Empty otherwise.
    /// </param>
    private static ComposedNode WalkFrame<TFrame>(FoldContext context, ref TFrame reader, ref StoredGroupHashes hashes, PbtNodeGroupWriter<TPath> writer,
        scoped in BoundaryNode input, ReadOnlySpan<PbtWriteOperation<TKey>> operations, PbtTraversalPath path, ReadOnlySpan<int> shardTable)
        where TFrame : struct, IGroupFrame<TKey, TPath>
    {
        int bitDepth = path.BitDepth;
        // Only a branch splitting inside the group has anything stored in it; the root position is the input itself.
        uint stored = !input.IsEmpty && !input.IsLeaf && input.BranchDepth < bitDepth + PbtFourLevelGroupGeometry.LevelsPerGroup
            ? reader.StoredPositions & ~(1u << PbtFourLevelGroupGeometry.RootPosition)
            : 0;
        writer.ReserveFirstBuffer(reader.PayloadLength);
        SortedWalk<TFrame> walk = new(context, ref reader, ref hashes, writer, path, input, stored, operations, shardTable);
        try
        {
            if (context.FoldQuota is not null) TryFoldSlotsInParallel(ref walk);
            ComposedNode root = Walk(ref walk, default, input.IsEmpty ? default : new Cover(CoverKind.Input, RootSource));
            Debug.Assert(walk.Next == operations.Length, "The walk consumes every operation of its frame.");
            return root.IsEmpty ? default : Land(ref reader, ref hashes, writer, root, PbtFourLevelGroupGeometry.RootPosition);
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
    /// <see cref="ForEachOnQuota"/> does.
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
        int runCount = PlanBucketRuns(counts[..foldCount], descendantBytes[..foldCount], fanOut, runEnds);
        if (runCount < 2) return;

        // Two touched boundary siblings need both old hashes to key their groups, so both are hashed together.
        for (int fold = 0; fold + 1 < foldCount; fold++)
        {
            int slot = slots[fold];
            if ((slot & 1) != 0 || slots[fold + 1] != slot + 1) continue;
            if (covers[fold].Kind != CoverKind.Stored || covers[fold + 1].Kind != CoverKind.Stored) continue;
            walk.Hashes.GetChildHashesPaired(ref walk.Reader, BoundaryPosition(slot), BoundaryPosition(slot + 1), out _, out _);
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
        ForEachOnQuota(context.FoldQuota!, runs.Count, run =>
        {
            for (int fold = run == 0 ? 0 : runs[run - 1]; fold < runs[run]; fold++)
                folds[fold].Fold(context, groupPath, bitDepth, foldedAhead);
        });
    }

    /// <summary>Runs <paramref name="work"/> for every index below <paramref name="count"/>, across threads as <paramref name="quota"/> allows.</summary>
    /// <remarks>
    /// Work runs on the calling thread while the quota has no free slot; the first slot taken admits a parallel loop over
    /// the indices still left, whose workers charge themselves as they start.
    /// </remarks>
    private static void ForEachOnQuota(ConcurrencyController quota, int count, Action<int> work)
    {
        int next = 0;
        for (; next < count - 1 && !quota.TryRequestConcurrencyQuota(); next++)
            work(next);
        if (next < count - 1)
        {
            int callerThreadId = Environment.CurrentManagedThreadId;
            int admissionSlotClaimed = 0;
            try
            {
                ParallelUnbalancedWork.For(next, count, ParallelUnbalancedWork.DefaultOptions,
                    () => TakeWorkerQuota(quota, callerThreadId, ref admissionSlotClaimed),
                    (index, tookQuota) =>
                    {
                        work(index);
                        return tookQuota;
                    },
                    tookQuota => ReturnWorkerQuota(quota, tookQuota));
            }
            finally
            {
                ReturnAdmissionSlot(quota, ref admissionSlotClaimed);
            }
        }
        else if (next < count)
        {
            work(next);
        }
    }

    /// <summary>Sorts a zone's operations, which the producer grouped by <paramref name="shardTable"/>'s shards, by sorting each shard in place, and replaces every set value with its leaf hash.</summary>
    /// <remarks>
    /// The shards lie in ascending shard order and every key in one shard shares the shard nibble, so sorted shards make a
    /// sorted zone. A zone wide enough for two workers sorts and hashes its shards across threads under the fold quota,
    /// taking the leaf hashes off the fold's own path and batching them for <see cref="Blake3Hash.HashMany"/>.
    /// </remarks>
    /// <param name="shardTable">The used-shard mask, then each used shard's count.</param>
    internal static void SortShards(FoldContext context, Span<PbtWriteOperation<TKey>> operations, ReadOnlySpan<int> shardTable)
    {
        int shardCount = BitOperations.PopCount((uint)shardTable[0]);
        if (context.FoldQuota is null || shardCount < 2 || operations.Length < 2 * context.FanOut.MinOperationsPerWorker)
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

    /// <summary>Folds a zone group whose operations <see cref="SortShards"/> sorted, leaving its root in <paramref name="result"/> anchored at <paramref name="resultDepth"/>.</summary>
    /// <remarks>
    /// A single recursion descends one bit at a time, splitting the sorted range where the bit turns to one and carrying
    /// the stored node covering each position alongside. The recursion returns in post-order, the order a group stores its
    /// positions in, so every node is appended to the group as soon as it is composed. A fold below a boundary slot hands
    /// back the final encoding of the node its parent group stores there. The shard table's counts are this frame's slot
    /// ranges, which the walk and its slot fan-out take as they are.
    /// </remarks>
    internal static void FoldZoneSorted<TFrame>(FoldContext context, ref TFrame reader, ref StoredGroupHashes hashes, PbtNodeGroupWriter<TPath> writer,
        BoundaryNode current, ReadOnlySpan<PbtWriteOperation<TKey>> operations, ref PbtTraversalPath path, int resultDepth, ReadOnlySpan<int> shardTable,
        ref FoldResult result)
        where TFrame : struct, IGroupFrame<TKey, TPath>
    {
        AssertSorted(operations);
        ComposedNode root = WalkFrame(context, ref reader, ref hashes, writer, current, operations, path, shardTable);
        TakeRoot(writer, path, resultDepth, root, ref result);
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
        internal readonly int Slot = slot;
        internal SlotNode Result;

        [SkipLocalsInit]
        internal void Fold(FoldContext context, TPath groupPath, int bitDepth, byte[] foldedAhead)
        {
            Span<byte> pathBuffer = stackalloc byte[PbtBitPrefix.ByteCount(TPath.MaxBitDepth)];
            PbtTraversalPath path = PbtTraversalPath.FromPath(pathBuffer, groupPath);
            path.AppendMut(Slot);
            // The fold below only reads its owner's depth and this slot's descendant size, so a stand-in carrying that
            // size replaces the frame, which stays with the calling thread.
            AbsentGroupFrame<TKey, TPath> owner = new(bitDepth, Slot, descendantBytes);
            using IPbtConcurrentWriter writer = context.Store.CreateWriter();
            FoldContext workerContext = new(context.Store, writer, context.MemoryProvider, context.FoldQuota, context.Operations, context.FanOut,
                context.PrefixlessBranchOmission);
            int slotDepth = bitDepth + PbtFourLevelGroupGeometry.LevelsPerGroup;
            Result = FoldSortedRange(workerContext, ref owner, boundary, context.Operations.AsSpan(offset, count), ref path,
                slotDepth, slotDepth, foldedAhead.AsSpan(Slot * MaxNodeLength, MaxNodeLength));
        }
    }

    private static SlotNode Copy(ReadOnlySpan<byte> node, in ValueHash256 hash, Span<byte> encoding)
    {
        node.CopyTo(encoding);
        return new SlotNode(node.Length, hash);
    }

    private static ValueHash256 HashBranch(ReadOnlySpan<byte> encoding) => Blake3Hash.Hash(PbtNodeReader.FromValidated(encoding).Preimage);

    /// <summary>The leaf hash of a set operation, which <see cref="SortShards"/> put in place of its value.</summary>
    private static ValueHash256 HashLeaf(in PbtWriteOperation<TKey> operation) => operation.Value;

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

    /// <summary>Encodes <paramref name="node"/> anchored at <paramref name="anchorDepth"/>, at or below its own anchor.</summary>
    private static SlotNode EncodeReanchored(scoped in BoundaryNode node, int anchorDepth, Span<byte> encoding)
    {
        if (node.IsEmpty) return default;
        if (node.IsLeaf) return EncodeLeaf(node.LeafKey, node.Hash, encoding);
        PbtNodeReader branch = node.Reader;
        int skippedBits = anchorDepth - node.AnchorDepth;
        return new SlotNode(EncodeReanchored(branch, skippedBits, branch.LeftHash, branch.RightHash, encoding), skippedBits == 0 ? node.Hash : default);
    }

    /// <summary>Encodes <paramref name="stored"/> with the first <paramref name="skippedBits"/> of its prefix dropped and the given child hashes.</summary>
    private static int EncodeReanchored(scoped PbtNodeReader stored, int skippedBits, in ValueHash256 left, in ValueHash256 right, Span<byte> encoding)
    {
        CompressedPrefix prefix = stored.Prefix;
        int bitCount = prefix.BitCount - skippedBits;
        int length = PbtNodeCodec.BranchLength(bitCount, stored.LeftKey.Length, stored.RightKey.Length);
        PbtNodeCodec.CreateBranchEncoding(encoding, bitCount, left, right);
        if (bitCount != 0) PbtBitPrefix.CopyBits(prefix.Bytes, skippedBits, bitCount, encoding[3..], 0);
        PbtNodeCodec.WriteBranchTrailer(encoding[PbtNodeCodec.BranchPreimageLength(bitCount)..length], stored.LeftKey, stored.RightKey);
        return length;
    }

    /// <summary>Encodes a group's root branch, stored at the group's depth, anchored higher at <paramref name="anchorDepth"/> after a prefix jump.</summary>
    /// <remarks>The bits between the two depths are the path's, which the jump skipped along the range's shared prefix.</remarks>
    private static int EncodeLifted(scoped PbtNodeReader root, scoped in PbtTraversalPath path, int anchorDepth, Span<byte> encoding)
    {
        CompressedPrefix prefix = root.Prefix;
        int liftedBits = path.BitDepth - anchorDepth;
        int bitCount = prefix.BitCount + liftedBits;
        int length = PbtNodeCodec.BranchLength(bitCount, root.LeftKey.Length, root.RightKey.Length);
        PbtNodeCodec.CreateBranchEncoding(encoding, bitCount, root.LeftHash, root.RightHash);
        PbtBitPrefix.CopyBits(path.Bytes, anchorDepth, liftedBits, encoding[3..], 0);
        if (prefix.BitCount != 0) PbtBitPrefix.CopyBits(prefix.Bytes, 0, prefix.BitCount, encoding[3..], liftedBits);
        PbtNodeCodec.WriteBranchTrailer(encoding[PbtNodeCodec.BranchPreimageLength(bitCount)..length], root.LeftKey, root.RightKey);
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
        int length = PbtNodeCodec.BranchLength(bitCount, leftKey.Length, rightKey.Length);
        PbtNodeCodec.CreateBranchEncoding(encoding, bitCount, firstIsLeft ? firstHash : secondHash, firstIsLeft ? secondHash : firstHash);
        if (bitCount != 0) PbtBitPrefix.CopyBits(first.Bytes, anchorDepth, bitCount, encoding[3..], 0);
        PbtNodeCodec.WriteBranchTrailer(encoding[PbtNodeCodec.BranchPreimageLength(bitCount)..length], leftKey.Bytes, rightKey.Bytes);
        return new SlotNode(length, default);
    }

    /// <summary>The hash of <paramref name="node"/> as the group at <paramref name="depth"/> is keyed by, as <see cref="BoundaryNode.HashAt"/>.</summary>
    [SkipLocalsInit]
    private static ValueHash256 HashAt(scoped in BoundaryNode node, int depth)
    {
        if (node.IsEmpty || node.IsLeaf || depth == node.AnchorDepth) return node.Hash;
        Span<byte> encoding = stackalloc byte[MaxNodeLength];
        int length = EncodeReanchored(node.Reader, depth - node.AnchorDepth, node.LeftHash, node.RightHash, encoding);
        return HashBranch(encoding[..length]);
    }

    /// <summary>Composes the node at <paramref name="local"/> from its <paramref name="cover"/> and the operations below it, appending it to the group.</summary>
    /// <remarks>
    /// Mirrors one frame of <see cref="Compose"/>: the returned node is the writer's last entry, or empty. The operations
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
        // Two touched boundary nodes each need their old hash to key the group below, so both are hashed together.
        if (local.Length == PbtFourLevelGroupGeometry.LevelsPerGroup - 1 && leftCover.Kind == CoverKind.Stored && rightCover.Kind == CoverKind.Stored
            && walk.Owns(local.Left) && walk.OwnsAfter(local.Left, local.Right))
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

        ComposeFrame frame = new(local, default);
        int leftPosition = position - local.Width;
        // Only read back once SettleLeftSorted wrote it, so it is not zeroed.
        Unsafe.SkipInit(out OmittedPreimage omittedLeft);
        bool leftPending = SettleLeftSorted(walk.Writer, walk.Path, leftPosition, left.RiseBitCount == 0 ? left : Land(ref walk.Reader, ref walk.Hashes, walk.Writer, left, leftPosition), ref frame, omittedLeft);
        ComposedNode right = Walk(ref walk, local.Right, rightCover);
        Debug.Assert(!right.IsEmpty, "A right half known to survive composes a node.");
        return AppendBranchSorted(walk.Writer, walk.Path, position, right.RiseBitCount == 0 ? right : Land(ref walk.Reader, ref walk.Hashes, walk.Writer, right, position - 1), ref frame,
            leftPending ? (ReadOnlySpan<byte>)omittedLeft : default);
    }

    /// <summary><see cref="SettleLeft"/>, keeping an omitted branch's preimage in <paramref name="omitted"/> so it is hashed together with its sibling.</summary>
    /// <returns>Whether <paramref name="omitted"/> holds the left child's preimage, still to be hashed.</returns>
    private static bool SettleLeftSorted(PbtNodeGroupWriter<TPath> writer, scoped in PbtTraversalPath path, int leftPosition, in ComposedNode left, ref ComposeFrame frame,
        Span<byte> omitted)
    {
        ReadOnlySpan<byte> encoding = writer.Entry(left.Offset, left.Length).Span;
        PbtNodeReader node = PbtNodeReader.FromValidated(encoding);
        frame.LeftIsLeaf = node.IsLeaf;
        frame.LeftHash = left.Hash;
        frame.LeftPreimageLength = 0;
        if (node.IsLeaf)
        {
            frame.LeftKey = TKey.Create(node.Key);
            writer.DropLast(leftPosition);
            return false;
        }
        if (writer.Omits(leftPosition, encoding))
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

    /// <summary><see cref="AppendBranch"/>, reading a pending left preimage from <paramref name="omittedLeft"/> when the left child was omitted.</summary>
    [SkipLocalsInit]
    private static ComposedNode AppendBranchSorted(PbtNodeGroupWriter<TPath> writer, scoped in PbtTraversalPath path, int position, in ComposedNode right, ref ComposeFrame frame,
        ReadOnlySpan<byte> omittedLeft)
    {
        int rightPosition = position - 1;
        ReadOnlySpan<byte> encoding = writer.Entry(right.Offset, right.Length).Span;
        PbtNodeReader node = PbtNodeReader.FromValidated(encoding);
        bool rightIsLeaf = node.IsLeaf;
        TKey rightKey = rightIsLeaf ? TKey.Create(node.Key) : default;
        ValueHash256 rightHash = right.Hash;
        ReadOnlySpan<byte> rightPreimage = rightHash == default ? node.Preimage : default;
        ReadOnlySpan<byte> leftPreimage = !omittedLeft.IsEmpty ? omittedLeft
            : frame.LeftPreimageLength == 0 ? default : writer.Entry(frame.LeftPreimageOffset, frame.LeftPreimageLength).Span;
        HashPendingPair(leftPreimage, ref frame.LeftHash, rightPreimage, ref rightHash);
        if (rightIsLeaf || writer.Omits(rightPosition, encoding))
            writer.DropLast(rightPosition);
        else
            writer.ValidateEntry(path, rightPosition, encoding);

        int leftKeyLength = frame.LeftIsLeaf ? frame.LeftKey.Length : 0;
        int rightKeyLength = rightIsLeaf ? rightKey.Length : 0;
        int offset = writer.WrittenCount;
        int length = PbtNodeCodec.BranchLength(0, leftKeyLength, rightKeyLength);
        Span<byte> branch = writer.Append(position, length);
        PbtNodeCodec.CreateBranchEncoding(branch, 0, frame.LeftHash, rightHash);
        Span<byte> trailer = branch[PbtNodeCodec.BranchPreimageLength(0)..];
        PbtNodeCodec.WriteBranchTrailer(trailer, leftKeyLength, rightKeyLength);
        if (frame.LeftIsLeaf) frame.LeftKey.Bytes.CopyTo(trailer[PbtNodeCodec.BranchTrailerHeaderLength..]);
        if (rightIsLeaf) rightKey.Bytes.CopyTo(trailer[(PbtNodeCodec.BranchTrailerHeaderLength + leftKeyLength)..]);
        return new(offset, length, default);
    }

    /// <summary><see cref="AppendImplicitBranch"/>, hashing the omitted levels below it pairwise.</summary>
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
            ValueHash256 seeded = hashes.KnownHash(position);
            if (seeded != default)
            {
                PbtNodeCodec.CreateBranchEncoding(branch, 0, seeded, seeded);
                PbtNodeCodec.WriteBranchTrailer(branch[PbtNodeCodec.BranchPreimageLength(0)..], 0, 0);
                if (writer.Omits(position, branch)) return new(offset, length, seeded) { ChildHashesPending = true };
            }

            hashes.GetChildHashesPaired(ref reader, position - width, position - 1, out ValueHash256 left, out ValueHash256 right);
            if (left != default && right != default)
            {
                PbtNodeCodec.CreateBranchEncoding(branch, 0, left, right);
                PbtNodeCodec.WriteBranchTrailer(branch[PbtNodeCodec.BranchPreimageLength(0)..], 0, 0);
                return new(offset, length, seeded);
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
            if (operation.Value != default) node = AppendLeaf(walk.Writer, local.Position, operation.Key, HashLeaf(operation));
            return true;
        }
        if (!operation.Key.Equals(walk.LeafKey(cover)))
        {
            if (operation.Value != default) return false;
            node = AppendUntouched(ref walk, local, cover);
            return true;
        }
        if (operation.Value == default) return true;
        ValueHash256 leafHash = HashLeaf(operation);
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
                    int copied = walk.Reader.CopyRange(walk.Writer, position - 2 * local.Width + 2, position + 1);
                    int length = walk.Reader.GetEncoding(position).Length;
                    return new ComposedNode(walk.Writer.WrittenCount - length, length, walk.Hashes.KnownHash(position));
                }
            case CoverKind.Implicit:
                CopyDescendants(ref walk, local);
                return AppendImplicitBranchSorted(ref walk.Reader, ref walk.Hashes, walk.Writer, position);
        }

        if (walk.IsLeaf(cover)) return AppendLeaf(walk.Writer, position, walk.LeafKey(cover), walk.LeafHash(cover));

        // A branch anchored above this position, whose compressed prefix passes through it.
        PbtNodeReader node = walk.Node(cover, out int anchorDepth);
        int depth = walk.BitDepth + local.Length;
        if (anchorDepth + node.Prefix.BitCount < walk.BitDepth + PbtFourLevelGroupGeometry.LevelsPerGroup) CopyDescendants(ref walk, local);
        return AppendReanchoredSorted(walk.Writer, position, depth - anchorDepth, node);
    }

    private static void CopyDescendants<TFrame>(scoped ref SortedWalk<TFrame> walk, NodeGroupPath local)
        where TFrame : struct, IGroupFrame<TKey, TPath>
    {
        int position = local.Position;
        int copied = walk.Reader.CopyRange(walk.Writer, position - 2 * local.Width + 2, position);
    }

    /// <summary>Appends a stored branch anchored <paramref name="skippedBits"/> above <paramref name="position"/>, with the rest of its compressed prefix.</summary>
    private static ComposedNode AppendReanchoredSorted(PbtNodeGroupWriter<TPath> writer, int position, int skippedBits, scoped PbtNodeReader stored)
    {
        int offset = writer.WrittenCount;
        int length = PbtNodeCodec.BranchLength(stored.Prefix.BitCount - skippedBits, stored.LeftKey.Length, stored.RightKey.Length);
        EncodeReanchored(stored, skippedBits, stored.LeftHash, stored.RightHash, writer.Append(position, length));
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
        SlotNode result = FoldSortedRange(walk.Context, ref walk.Reader, boundary, operations, ref slotPath, slotDepth, slotDepth, encoding);
        slotPath.Truncate(bitDepth);
        walk.Writer.AddDescendantDelta(slot, result.SizeDelta);
        return result;
    }

    /// <summary>Whether anything under <paramref name="local"/> survives deletions that are all <paramref name="operations"/> hold.</summary>
    /// <remarks>
    /// A sibling must be known to survive before its left neighbour is settled, as in <see cref="Compose"/>. The keys are
    /// matched against the cover down to the boundary slots, whose groups are folded ahead and kept for the walk.
    /// </remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool Survives<TFrame>(scoped ref SortedWalk<TFrame> walk, NodeGroupPath local, Cover cover, ReadOnlySpan<PbtWriteOperation<TKey>> operations)
        where TFrame : struct, IGroupFrame<TKey, TPath>
    {
        if (cover.IsEmpty) return false;
        if (operations.IsEmpty) return true;
        if (walk.IsLeaf(cover)) return !Contains(operations, walk.LeafKey(cover));
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
        internal readonly CoverKind Kind = kind;
        internal readonly byte Position = (byte)position;
        internal readonly bool Right = right;
        internal bool IsEmpty => Kind == CoverKind.Empty;
    }

    /// <summary>The frame one in-frame recursion rebuilds, and the slot results it folded ahead of the walk.</summary>
    private ref struct SortedWalk<TFrame>
        where TFrame : struct, IGroupFrame<TKey, TPath>
    {
        internal readonly FoldContext Context;
        internal ref TFrame Reader;
        internal ref StoredGroupHashes Hashes;
        internal readonly PbtNodeGroupWriter<TPath> Writer;
        internal readonly PbtTraversalPath Path;
        internal readonly int BitDepth;
        private readonly BoundaryNode _input;
        private readonly uint _stored;
        /// <summary>The encodings of the slots folded ahead of the walk, <see cref="MaxNodeLength"/> bytes per slot.</summary>
        internal byte[]? FoldedAhead;
        internal SlotNode[]? FoldedAheadNodes;
        internal int FoldedAheadMask;
        /// <summary>The frame's operations, in key order.</summary>
        internal readonly ReadOnlySpan<PbtWriteOperation<TKey>> Operations;
        /// <summary>The producer's used-slot mask and per-slot counts when it grouped the operations by this frame's slots, or empty.</summary>
        private readonly ReadOnlySpan<int> _shardTable;
        /// <summary>The index of the first operation no position has consumed yet.</summary>
        internal int Next;
        /// <summary>The boundary slot of the operation at <see cref="Next"/>, or one past the last slot once every operation is consumed.</summary>
        /// <remarks>Read only when the cursor moves, so a position checks whether it owns the next operation without reading its key.</remarks>
        private int _nextSlot;
        /// <summary>The boundary slot of the operation after <see cref="Next"/>, or -1 until it is read.</summary>
        private int _followingSlot;

        /// <summary>Whether the operation at the cursor lies under <paramref name="local"/>.</summary>
        internal readonly bool Owns(NodeGroupPath local) => Covers(_nextSlot, local);

        /// <summary>Whether the operation after the cursor lies under <paramref name="local"/> too.</summary>
        internal bool OwnsFollowing(NodeGroupPath local)
        {
            if (_followingSlot < 0) _followingSlot = SlotAt(Next + 1);
            return Covers(_followingSlot, local);
        }

        /// <summary>Whether the first operation past those under <paramref name="first"/> lies under <paramref name="second"/>.</summary>
        internal readonly bool OwnsAfter(NodeGroupPath first, NodeGroupPath second) => Covers(SlotAt(Next + CountOwned(first)), second);

        /// <summary>How many operations from the cursor on lie under <paramref name="local"/>.</summary>
        internal readonly int CountOwned(NodeGroupPath local) => CountOwned(Operations, Next, local, BitDepth);

        internal static int CountOwned(ReadOnlySpan<PbtWriteOperation<TKey>> operations, int start, NodeGroupPath local, int bitDepth)
        {
            int end = start;
            while (end < operations.Length && Covers(BoundarySlot(operations[end].Key.Bytes, bitDepth), local)) end++;
            return end - start;
        }

        /// <summary>The operations at the cursor under <paramref name="local"/>, left unconsumed.</summary>
        internal readonly ReadOnlySpan<PbtWriteOperation<TKey>> Peek(NodeGroupPath local) => Operations.Slice(Next, CountOwned(local));

        /// <summary>Consumes the operations at the cursor in the boundary slot <paramref name="local"/>.</summary>
        /// <remarks>Each key is read once, to find where the slot ends; a frame with a shard table reads none.</remarks>
        internal ReadOnlySpan<PbtWriteOperation<TKey>> Take(NodeGroupPath local)
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
        internal void Advance(int count)
        {
            Next += count;
            _nextSlot = SlotAt(Next);
            _followingSlot = -1;
        }

        /// <summary>Whether any operation at the cursor under <paramref name="local"/> sets a value rather than deleting one.</summary>
        internal readonly bool HasSet(NodeGroupPath local)
        {
            for (int index = Next, slot = _nextSlot; Covers(slot, local); slot = SlotAt(++index))
                if (Operations[index].Value != default) return true;
            return false;
        }

        /// <summary>The boundary slot of the operation at <paramref name="index"/>, from the shard table when the frame has one.</summary>
        internal readonly int SlotAt(int index)
        {
            if (index >= Operations.Length) return PbtFourLevelGroupGeometry.BoundarySlots;
            if (_shardTable.IsEmpty) return BoundarySlot(Operations[index].Key.Bytes, BitDepth);
            int end = 0;
            int rank = 1;
            for (int mask = _shardTable[0]; mask != 0; mask &= mask - 1)
            {
                end += _shardTable[rank++];
                if (index < end) return BitOperations.TrailingZeroCount(mask);
            }
            return PbtFourLevelGroupGeometry.BoundarySlots;
        }

        /// <summary>The index past the operations from <paramref name="start"/> on in boundary slot <paramref name="slot"/>.</summary>
        internal readonly int SlotEnd(int start, int slot)
        {
            if (!_shardTable.IsEmpty)
            {
                int end = 0;
                int rank = 1;
                for (int mask = _shardTable[0] & ((2 << slot) - 1); mask != 0; mask &= mask - 1) end += _shardTable[rank++];
                return end;
            }
            int index = start + 1;
            while (index < Operations.Length && BoundarySlot(Operations[index].Key.Bytes, BitDepth) == slot) index++;
            return index;
        }

        /// <summary>Whether a key in boundary slot <paramref name="slot"/> lies under <paramref name="local"/>; no key lies under one past the last slot.</summary>
        private static bool Covers(int slot, NodeGroupPath local) =>
            ((slot ^ local.Slot) >> (PbtFourLevelGroupGeometry.LevelsPerGroup - local.Length)) == 0;

        internal SortedWalk(FoldContext context, ref TFrame reader, ref StoredGroupHashes hashes, PbtNodeGroupWriter<TPath> writer,
            PbtTraversalPath path, in BoundaryNode input, uint stored, ReadOnlySpan<PbtWriteOperation<TKey>> operations, ReadOnlySpan<int> shardTable)
        {
            Operations = operations;
            _shardTable = shardTable;
            Context = context;
            Reader = ref reader;
            Hashes = ref hashes;
            Writer = writer;
            Path = path;
            BitDepth = path.BitDepth;
            _nextSlot = SlotAt(0);
            _followingSlot = -1;
            _input = input;
            _stored = stored;
        }

        internal readonly bool IsLeaf(Cover cover) => cover.Kind == CoverKind.InlineLeaf || (cover.Kind == CoverKind.Input && _input.IsLeaf);

        internal readonly TKey LeafKey(Cover cover) => cover.Kind == CoverKind.Input ? _input.LeafKey : TKey.Create(LeafKeyBytes(cover));

        internal readonly ValueHash256 LeafHash(Cover cover)
        {
            if (cover.Kind == CoverKind.Input) return _input.Hash;
            PbtNodeReader parent = Parent(cover);
            return cover.Right ? parent.RightHash : parent.LeftHash;
        }

        private readonly ReadOnlySpan<byte> LeafKeyBytes(Cover cover)
        {
            if (cover.Kind == CoverKind.Input)
            {
                PbtNodeReader input = _input.Reader;
                return _input.Source switch
                {
                    LeafSource.ParentLeft => input.LeftKey,
                    LeafSource.ParentRight => input.RightKey,
                    _ => input.Key,
                };
            }
            PbtNodeReader parent = Parent(cover);
            return cover.Right ? parent.RightKey : parent.LeftKey;
        }

        private readonly PbtNodeReader Parent(Cover cover) => cover.Position == RootSource
            ? _input.Reader
            : PbtNodeReader.FromValidated(Reader.GetEncoding(cover.Position).Span);

        /// <summary>The branch <paramref name="cover"/> holds, with the absolute depth its compressed prefix starts at.</summary>
        internal readonly PbtNodeReader Node(Cover cover, out int anchorDepth)
        {
            if (cover.Kind == CoverKind.Input)
            {
                anchorDepth = _input.AnchorDepth;
                return _input.Reader;
            }
            anchorDepth = BitDepth + PbtFourLevelGroupGeometry.LocalPathOf(cover.Position).Length;
            return PbtNodeReader.FromValidated(Reader.GetEncoding(cover.Position).Span);
        }

        /// <summary>The covers of <paramref name="local"/>'s two children.</summary>
        internal void ChildCovers(NodeGroupPath local, Cover cover, out Cover left, out Cover right)
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
                if (GetBit(LeafKeyBytes(cover), depth) == 0) left = cover;
                else right = cover;
                return;
            }

            PbtNodeReader node = Node(cover, out int anchorDepth);
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
            left = node.LeftKey.IsEmpty ? ChildNode(local.Left, node.LeftHash) : new Cover(CoverKind.InlineLeaf, source, right: false);
            right = node.RightKey.IsEmpty ? ChildNode(local.Right, node.RightHash) : new Cover(CoverKind.InlineLeaf, source, right: true);
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
        internal Cover CoverAt(int slot)
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
        internal readonly BoundaryNode Boundary(Cover cover) => cover.Kind switch
        {
            CoverKind.Empty => default,
            CoverKind.Input => _input,
            CoverKind.Stored => Reader.TakeBoundaryNode(cover.Position, Hashes.GetHash(ref Reader, cover.Position)),
            CoverKind.InlineLeaf => cover.Position == RootSource ? _input.InlineLeaf(cover.Right) : Reader.TakeInlineLeaf(cover.Position, cover.Right),
            _ => throw new InvalidDataException("A boundary node is never left implicit."),
        };
    }
}
