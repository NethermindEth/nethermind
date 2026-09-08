// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Core.Buffers;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using static Nethermind.Pbt.TrieUpdater;

namespace Nethermind.Pbt;

/// <summary>Applies complete-key mutations to a canonical compressed EIP-8297 tree.</summary>
public static partial class TrieUpdater
{
    internal static int GetBit(ReadOnlySpan<byte> bytes, int bit) => (bytes[bit >> 3] >> (7 - (bit & 7))) & 1;

    internal enum NodeKind : byte { Empty, Reference, Original, Leaf, Branch }

    private const int FullSortThreshold = PbtFourLevelGroupGeometry.BoundarySlots;

    /// <summary>Range knowledge and producer buckets carried through one traversal frame.</summary>
    internal readonly ref struct BucketPlan(ReadOnlySpan<int> precalculated, int depth, int branchDepth, bool isSorted, bool prefixesValidated, Span<byte> buffer = default)
    {
        private readonly Span<byte> _buffer = buffer;

        internal BucketPlan WithBuffer(Span<byte> buffer) =>
            new(Precalculated, Depth, BranchDepth, IsSorted, PrefixesValidated, buffer);

        internal ReadOnlySpan<int> Precalculated { get; } = precalculated;
        internal int Depth { get; } = depth;
        internal int BranchDepth { get; } = branchDepth;
        internal bool IsSorted { get; } = isSorted;
        internal bool PrefixesValidated { get; } = prefixesValidated;

        /// <summary>Gets the scratch buffer size in bytes required by this plan.</summary>
        internal int GetBufferSize(int operationCount) => !Precalculated.IsEmpty
            ? 0
            : sizeof(int) * Math.Min(operationCount, BranchDepth >= Depth + PbtFourLevelGroupGeometry.LevelsPerGroup
                ? 1 : PbtFourLevelGroupGeometry.BoundarySlots);

        internal PartitionOutcome BucketSort<TKey>(Span<PbtWriteOperation<TKey>> operations, TrieUpdaterMetrics? metrics) where TKey : struct, IPbtKey<TKey>
        {
            if (!Precalculated.IsEmpty)
            {
                metrics?.IncrementPrecalculatedLevels();
                int mask = Precalculated[0];
                int bound = BitOperations.IsPow2(mask) ? Depth + PbtFourLevelGroupGeometry.LevelsPerGroup : Depth;
                return new(mask, Precalculated.Slice(1, BitOperations.PopCount((uint)mask)), WithRangeKnowledge(Math.Max(BranchDepth, bound), PrefixesValidated));
            }

            Span<int> counts = MemoryMarshal.Cast<byte, int>(_buffer);
            int branchDepth = operations.Length == 1 ? operations[0].Key.BitLength : BranchDepth;
            branchDepth = Math.Max(branchDepth, Depth);
            BucketPlan plan = WithRangeKnowledge(branchDepth, PrefixesValidated);
            if (!operations.IsEmpty && branchDepth >= Depth + PbtFourLevelGroupGeometry.LevelsPerGroup)
            {
                metrics?.IncrementSynthesizedSingleBuckets();
                int slot = BoundarySlot(operations[0].Key, Depth);
                counts[0] = operations.Length;
                return new(1 << slot, counts[..1], plan);
            }

            if (IsSorted)
                metrics?.IncrementSortedLevels();
            else if (operations.Length <= FullSortThreshold)
            {
                if (operations.Length > 1)
                {
                    metrics?.IncrementFullKeySorts();
                    if (operations.Length <= 3) SortTiny(operations);
                    else operations.Sort(OperationKeyComparer<TKey>.Instance);
                }
                plan = new(default, Depth, branchDepth, true, PrefixesValidated, _buffer);
            }
            else
            {
                metrics?.IncrementRadixPartitions();
                int mask = BucketizeLarge(operations, Depth, counts, metrics, out branchDepth);
                return new(mask, counts[..BitOperations.PopCount((uint)mask)], plan.WithRangeKnowledge(branchDepth, PrefixesValidated));
            }

            int usedMask = 0;
            int countIndex = -1;
            int previousSlot = -1;
            foreach (PbtWriteOperation<TKey> operation in operations)
            {
                int slot = BoundarySlot(operation.Key, Depth);
                if (slot != previousSlot)
                {
                    counts[++countIndex] = 0;
                    previousSlot = slot;
                    usedMask |= 1 << slot;
                }
                counts[countIndex]++;
            }
            if (BitOperations.IsPow2(usedMask))
            {
                metrics?.IncrementOperationPrefixComparisons();
                branchDepth = operations[0].Key.FirstDifferingBit(operations[^1].Key, Depth);
            }
            return new(usedMask, counts[..(countIndex + 1)], plan.WithRangeKnowledge(branchDepth, PrefixesValidated));
        }

        internal BucketPlan WithRangeKnowledge(int branchDepth, bool prefixesValidated) =>
            new(Precalculated, Depth, branchDepth, IsSorted, prefixesValidated, _buffer);

        internal BucketPlan ForChild() =>
            new(default, Depth + PbtFourLevelGroupGeometry.LevelsPerGroup, BranchDepth, IsSorted, PrefixesValidated);

        internal BucketPlan AfterJump(int depth) => new(default, depth, BranchDepth, IsSorted, PrefixesValidated);

        internal BucketPlan AfterFiltering(bool preservesOrder) => new(default, Depth, BranchDepth, IsSorted && preservesOrder, PrefixesValidated);
    }

    /// <summary>The touched buckets and range knowledge established by partitioning.</summary>
    internal readonly ref struct PartitionOutcome(int usedMask, ReadOnlySpan<int> counts, BucketPlan plan)
    {
        internal int UsedMask { get; } = usedMask;
        /// <summary>Non-empty bucket counts in ascending slot order.</summary>
        internal ReadOnlySpan<int> Counts { get; } = counts;
        internal BucketPlan Plan { get; } = plan;
    }

    private static void SortTiny<TKey>(Span<PbtWriteOperation<TKey>> operations) where TKey : struct, IPbtKey<TKey>
    {
        CompareAndSwap(operations, 0, 1);
        if (operations.Length == 2) return;
        CompareAndSwap(operations, 1, 2);
        CompareAndSwap(operations, 0, 1);
    }

    private static void CompareAndSwap<TKey>(Span<PbtWriteOperation<TKey>> operations, int first, int second) where TKey : struct, IPbtKey<TKey>
    {
        if (operations[first].Key.CompareTo(operations[second].Key) > 0)
            (operations[first], operations[second]) = (operations[second], operations[first]);
    }

    private sealed class OperationKeyComparer<TKey> : IComparer<PbtWriteOperation<TKey>> where TKey : struct, IPbtKey<TKey>
    {
        internal static readonly OperationKeyComparer<TKey> Instance = new();
        public int Compare(PbtWriteOperation<TKey> left, PbtWriteOperation<TKey> right) => left.Key.CompareTo(right.Key);
    }

    private static int BucketizeLarge<TKey>(Span<PbtWriteOperation<TKey>> operations, int groupDepth, Span<int> compactCounts, TrieUpdaterMetrics? metrics, out int branchDepth) where TKey : struct, IPbtKey<TKey>
    {
        Span<int> counts = stackalloc int[PbtFourLevelGroupGeometry.BoundarySlots];
        counts.Clear();
        int usedMask = 0;
        TKey firstKey = operations[0].Key;
        branchDepth = firstKey.BitLength;
        for (int index = 0; index < operations.Length; index++)
        {
            TKey key = operations[index].Key;
            if (index != 0 && branchDepth >= groupDepth + PbtFourLevelGroupGeometry.LevelsPerGroup)
            {
                metrics?.IncrementOperationPrefixComparisons();
                branchDepth = Math.Min(branchDepth, firstKey.FirstDifferingBit(key, groupDepth));
            }
            int bucket = BoundarySlot(key, groupDepth);
            counts[bucket]++;
            usedMask |= 1 << bucket;
        }
        Span<int> offsets = stackalloc int[PbtFourLevelGroupGeometry.BoundarySlots + 1];
        offsets[0] = 0;
        for (int bucket = 0; bucket < PbtFourLevelGroupGeometry.BoundarySlots; bucket++)
            offsets[bucket + 1] = offsets[bucket] + counts[bucket];
        int countIndex = 0;
        for (int mask = usedMask; mask != 0; mask &= mask - 1)
            compactCounts[countIndex++] = counts[BitOperations.TrailingZeroCount(mask)];
        if (BitOperations.IsPow2(usedMask)) return usedMask;

        branchDepth = groupDepth;
        Span<int> next = stackalloc int[PbtFourLevelGroupGeometry.BoundarySlots];
        offsets[..PbtFourLevelGroupGeometry.BoundarySlots].CopyTo(next);

        for (int mask = usedMask; mask != 0; mask &= mask - 1)
        {
            int bucket = BitOperations.TrailingZeroCount(mask);
            int end = offsets[bucket + 1];
            while (next[bucket] < end)
            {
                int index = next[bucket];
                int destination = BoundarySlot(operations[index].Key, groupDepth);
                if (destination == bucket)
                {
                    next[bucket]++;
                    continue;
                }

                (operations[index], operations[next[destination]]) =
                    (operations[next[destination]], operations[index]);
                next[destination]++;
            }
        }
        return usedMask;
    }

    internal static int BoundarySlot<TKey>(TKey key, int groupDepth) where TKey : struct, IPbtKey<TKey> => BoundarySlot(key.Bytes, groupDepth);

    internal static int BoundarySlot(ReadOnlySpan<byte> key, int groupDepth)
    {
        byte value = key[groupDepth >> 3];
        return (value >> (4 - (groupDepth & 4))) & 0x0F;
    }

}

internal static class TrieUpdater<TKey, TPath>
    where TKey : struct, IPbtKey<TKey>
    where TPath : class, IPbtNodePath<TPath>
{
    private static readonly TPath RootPath = TPath.Create([], 0);

    /// <summary>Applies <paramref name="changes"/> and returns the resulting canonical root.</summary>
    /// <remarks>
    /// Effective mutations are folded through the tree as traversal-local partitioned ranges, so mutations
    /// sharing a path share one traversal. Each completed frame publishes its complete node group.
    /// </remarks>
    public static ValueHash256 UpdateRoot(IPbtStore store, in ValueHash256 currentRoot, PbtWriteBatch<TKey> changes) =>
        UpdateRoot(store, currentRoot, changes, null);

    internal static ValueHash256 UpdateRoot(
        IPbtStore store,
        in ValueHash256 currentRoot,
        PbtWriteBatch<TKey> changes,
        TrieUpdaterMetrics? metrics,
        IRefCountingMemoryProvider? memoryProvider = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(changes);
        BucketPlan plan = changes.Plan;
        changes.Consume(out ArrayPoolList<PbtWriteOperation<TKey>> operations, out ArrayPoolList<int> table);
        using ArrayPoolList<PbtWriteOperation<TKey>> ownedOperations = operations;
        using ArrayPoolList<int> ownedTable = table;
        return UpdateRoot(store, currentRoot, operations.AsSpan(), changes.ShardNibbleIndex == 0 ? plan : default, metrics, memoryProvider);
    }

    internal static ValueHash256 UpdateRoot(IPbtStore store, in ValueHash256 currentRoot, PbtWriteBatchSet<TKey> changes, TrieUpdaterMetrics? metrics = null, IRefCountingMemoryProvider? memoryProvider = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(changes);
        changes.Consume(out ArrayPoolList<PbtWriteOperation<TKey>> operations, out ArrayPoolList<int> precalculated);
        using ArrayPoolList<PbtWriteOperation<TKey>> ownedOperations = operations;
        using ArrayPoolList<int> ownedTable = precalculated;
        return UpdateRoot(store, currentRoot, operations.AsSpan(), new(precalculated.AsSpan(), 0, 0, false, false), metrics, memoryProvider);
    }

    private static ValueHash256 UpdateRoot(IPbtStore store, in ValueHash256 currentRoot, Span<PbtWriteOperation<TKey>> operations, BucketPlan plan, TrieUpdaterMetrics? metrics, IRefCountingMemoryProvider? memoryProvider)
    {
        if (operations.IsEmpty) return currentRoot;
        using GroupMutationFrame group = new(store, RootPath, metrics, memoryProvider);
        Subtree root = group.Take(RootPath, allowAbsent: true);
        Subtree result = default;
        try
        {
            result = FoldMutations(store, metrics, group, ref root, operations, plan);
            ValueHash256 hash = group.Write(PbtFourLevelGroupGeometry.RootPosition, 0, ref result);
            group.Flush();
            return hash;
        }
        finally
        {
            root.Dispose();
            result.Dispose();
        }
    }

    private static Subtree FoldMutations(IPbtStore store, TrieUpdaterMetrics? metrics, GroupMutationFrame ownerGroup,
        ref Subtree input, Span<PbtWriteOperation<TKey>> operations, BucketPlan plan)
    {
        Subtree current = Subtree.Move(ref input);
        try { return FoldMutationsCore(store, metrics, ownerGroup, ref current, operations, plan); }
        finally { current.Dispose(); }
    }

    private static Subtree FoldMutationsCore(
        IPbtStore store,
        TrieUpdaterMetrics? metrics,
        GroupMutationFrame ownerGroup,
        ref Subtree current,
        Span<PbtWriteOperation<TKey>> operations,
        BucketPlan plan)
    {
        int depth = plan.Depth;
        ownerGroup.Resolve(ref current);
        if (operations.IsEmpty) return Subtree.Move(ref current);

        if (current.IsEmpty)
        {
            if (operations.Length == 1)
                return operations[0].Value == default ? default : new Subtree(operations[0]);
        }
        else if (current.IsLeaf)
        {
            if (operations.Length == 1)
            {
                PbtWriteOperation<TKey> operation = operations[0];
                if (operation.Key.Equals(current.Key))
                    return operation.Value == default ? default : new Subtree(operation, current.Path);
                if (operation.Value == default) return Subtree.Move(ref current);
            }
        }

        if (depth > 0 && (depth & 7) == 0)
        {
            int terminalIndex = -1;
            for (int index = 0; index < operations.Length; index++)
            {
                if (operations[index].Key.BitLength != depth) continue;
                terminalIndex = index;
                break;
            }
            bool hasTerminalLeaf = current.IsLeaf && current.Key.BitLength == depth;
            if (terminalIndex >= 0 || hasTerminalLeaf)
            {
                // EIP-8297 prefix freedom applies to surviving keys, after both buckets have been folded.
                Subtree terminal = hasTerminalLeaf ? Subtree.Move(ref current) : default;
                Subtree descendants = default;
                try
                {
                    if (terminalIndex >= 0)
                    {
                        PbtWriteOperation<TKey> operation = operations[terminalIndex];
                        operations[..terminalIndex].CopyTo(operations[1..]);
                        operations[0] = operation;
                        terminal = FoldMutations(store, metrics, ownerGroup, ref terminal, operations[..1], plan);
                        operations = operations[1..];
                        plan = plan.AfterFiltering(preservesOrder: true);
                    }
                    descendants = FoldMutations(store, metrics, ownerGroup, ref current, operations, plan);
                    if (terminal.IsEmpty) return Subtree.Move(ref descendants);
                    if (!descendants.IsEmpty) throw new ArgumentException("Tree keys must be prefix-free.", nameof(operations));
                    return Subtree.Move(ref terminal);
                }
                finally
                {
                    terminal.Dispose();
                    descendants.Dispose();
                }
            }
        }

        plan = EstablishRangeKnowledge(current, operations, plan, metrics);
        Span<byte> buffer = stackalloc byte[plan.GetBufferSize(operations.Length)];
        bool hasComputedPartition = false;
        scoped PartitionOutcome partition = default;
        if (plan.Precalculated.IsEmpty && plan.BranchDepth <= depth)
        {
            partition = plan.WithBuffer(buffer).BucketSort(operations, metrics);
            plan = new(default, depth, partition.Plan.BranchDepth, partition.Plan.IsSorted, partition.Plan.PrefixesValidated);
            hasComputedPartition = true;
        }
        int branchDepth = FindBranchDepth(current, operations[0].Key, plan);
        int groupDepth = branchDepth / PbtFourLevelGroupGeometry.LevelsPerGroup * PbtFourLevelGroupGeometry.LevelsPerGroup;
        if (groupDepth != depth)
        {
            if (!plan.Precalculated.IsEmpty && BitOperations.IsPow2(plan.Precalculated[0]))
            {
                metrics?.IncrementPrecalculatedLevels();
                return FoldMutations(store, metrics, ownerGroup, ref current, operations,
                    plan.ForChild());
            }

            // The range's prefix survives the jump; the existing subtree only limits how far we can jump.
            return FoldMutations(store, metrics, ownerGroup, ref current, operations, plan.AfterJump(groupDepth));
        }

        if (ownerGroup.BitDepth == depth)
            return hasComputedPartition
                ? FoldBoundaryFromPartition(store, metrics, ownerGroup, ref current, operations, partition)
                : FoldBoundary(store, metrics, ownerGroup, ref current, operations, plan);

        using GroupMutationFrame group = new(store, PbtPathOperations.FromKey<TPath>(operations[0].Key.Bytes, depth), metrics, ownerGroup.MemoryProvider);
        Subtree result = hasComputedPartition
            ? FoldBoundaryFromPartition(store, metrics, group, ref current, operations, partition)
            : FoldBoundary(store, metrics, group, ref current, operations, plan);
        try
        {
            group.Flush();
            return Subtree.Move(ref result);
        }
        finally { result.Dispose(); }
    }

    internal static Subtree FoldBoundary(
        IPbtStore store,
        TrieUpdaterMetrics? metrics,
        GroupMutationFrame group,
        ref Subtree current,
        Span<PbtWriteOperation<TKey>> operations,
        BucketPlan plan)
    {
        Span<byte> buffer = stackalloc byte[plan.GetBufferSize(operations.Length)];
        PartitionOutcome partition = plan.WithBuffer(buffer).BucketSort(operations, metrics);
        return FoldBoundaryFromPartition(store, metrics, group, ref current, operations, partition);
    }

    private static Subtree FoldBoundaryFromPartition(
        IPbtStore store,
        TrieUpdaterMetrics? metrics,
        GroupMutationFrame group,
        ref Subtree current,
        Span<PbtWriteOperation<TKey>> operations,
        PartitionOutcome partition)
    {
        int depth = partition.Plan.Depth;
        RefList16<Subtree> boundaryBuffer = new(PbtFourLevelGroupGeometry.BoundarySlots);
        Span<Subtree> boundaries = boundaryBuffer.AsSpan();
        try
        {
            Decompose(group, ref current, depth, boundaries);

            int offset = 0;
            int countIndex = 0;
            for (int mask = partition.UsedMask; mask != 0; mask &= mask - 1)
            {
                int slot = BitOperations.TrailingZeroCount(mask);
                int count = partition.Counts[countIndex++];
                Span<PbtWriteOperation<TKey>> bucket = operations.Slice(offset, count);
                offset += count;
                boundaries[slot] = FoldMutations(
                    store, metrics, group, ref boundaries[slot], bucket, partition.Plan.ForChild());
            }

            return Compose(group, boundaries, partition.UsedMask);
        }
        finally { Dispose(boundaries); }
    }

    internal static Subtree Compose(GroupMutationFrame group, Span<Subtree> boundaries, int touchedMask)
    {
        int occupied = 0;
        for (int slot = 0; slot < boundaries.Length; slot++)
        {
            // Consume original boundary positions before ordered emission can pass their old locations.
            group.Resolve(ref boundaries[slot]);
            if (!boundaries[slot].IsEmpty) occupied |= 1 << slot;
        }
        if (occupied == 0) return default;

        ComposeFrameBuffer frames = default;
        int frameCount = 1;
        frames[0] = new(occupied, default);
        Subtree result = default;
        try
        {
            while (frameCount != 0)
            {
                ref ComposeFrame frame = ref frames[frameCount - 1];
                int halfWidth = frame.Path.Width / 2;
                if (frame.Stage == ComposeStage.LeftCompleted)
                {
                    // The left root must be emitted before any descendants of the right subtree.
                    frame.LeftHash = group.Write(frame.Path.Position - frame.Path.Width, group.GroupKey.BitDepth + frame.Path.Length + 1, ref result);
                    frame.Stage = ComposeStage.RightCompleted;
                    if (halfWidth == 1)
                        result = Subtree.Move(ref boundaries[frame.Path.Slot + 1]);
                    else
                        frames[frameCount++] = new(frame.Occupied >> halfWidth, frame.Path.Right);
                    continue;
                }
                if (frame.Stage == ComposeStage.RightCompleted)
                {
                    ValueHash256 rightHash = group.Write(frame.Path.Position - 1, group.GroupKey.BitDepth + frame.Path.Length + 1, ref result);
                    TPath branchPath = BoundaryPath(group.GroupKey, frame.Path.Slot, frame.Path.Length);
                    result = new Subtree(branchPath, frame.LeftHash, rightHash);
                    frameCount--;
                    continue;
                }
                int rangeMask = ((1 << frame.Path.Width) - 1) << frame.Path.Slot;
                if ((touchedMask & rangeMask) == 0 && group.TryCopyUnchangedSubtree(frame.Path, out result))
                {
                    Dispose(boundaries.Slice(frame.Path.Slot, frame.Path.Width));
                    frameCount--;
                    continue;
                }
                if (BitOperations.IsPow2(frame.Occupied))
                {
                    result = Subtree.Move(ref boundaries[frame.Path.Slot + BitOperations.TrailingZeroCount(frame.Occupied)]);
                    frameCount--;
                    continue;
                }

                int leftMask = frame.Occupied & ((1 << halfWidth) - 1);
                int rightMask = frame.Occupied >> halfWidth;
                if (leftMask == 0)
                {
                    frame = new(rightMask, frame.Path.Right);
                    continue;
                }
                if (rightMask == 0)
                {
                    frame = new(leftMask, frame.Path.Left);
                    continue;
                }

                frame.Stage = ComposeStage.LeftCompleted;
                if (halfWidth == 1)
                    result = Subtree.Move(ref boundaries[frame.Path.Slot]);
                else
                    frames[frameCount++] = new(leftMask, frame.Path.Left);
            }
            return Subtree.Move(ref result);
        }
        finally { result.Dispose(); }
    }

    private enum ComposeStage : byte { Descend, LeftCompleted, RightCompleted }

    private struct ComposeFrame(int occupied, NodeGroupPath path)
    {
        internal int Occupied = occupied;
        internal NodeGroupPath Path = path;
        internal ComposeStage Stage;
        internal ValueHash256 LeftHash;
    }

    [InlineArray(PbtFourLevelGroupGeometry.LevelsPerGroup)]
    private struct ComposeFrameBuffer
    {
        private ComposeFrame _element;
    }

    internal static void Decompose(GroupMutationFrame group, ref Subtree current, int depth, Span<Subtree> boundaries)
    {
        if (current.IsEmpty) return;
        int boundaryDepth = depth + PbtFourLevelGroupGeometry.LevelsPerGroup;
        if (current.IsReference && current.Path!.BitDepth == boundaryDepth)
        {
            boundaries[BoundarySlot(current.Path.Path, depth)] = Subtree.Move(ref current);
            return;
        }

        group.Resolve(ref current);
        if (!current.IsEmpty && current.IsLeaf)
        {
            boundaries[BoundarySlot(current.Key.Bytes, depth)] = Subtree.Move(ref current);
            return;
        }

        int branchDepth = current.Path!.BitDepth + current.PrefixBitCount;
        if (branchDepth >= boundaryDepth)
        {
            int slot = 0;
            for (int bit = depth; bit < boundaryDepth; bit++)
                slot = (slot << 1) | PrefixBit(current, bit);
            boundaries[slot] = Subtree.Move(ref current);
            return;
        }

        Subtree left = new(current.LeftHash, PbtPathOperations.Append<TPath>(current.Path, current.Prefix, current.PrefixBitCount, 0));
        Subtree right = new(current.RightHash, PbtPathOperations.Append<TPath>(current.Path, current.Prefix, current.PrefixBitCount, 1));
        current.Dispose();
        try
        {
            Decompose(group, ref left, depth, boundaries);
            Decompose(group, ref right, depth, boundaries);
        }
        finally
        {
            left.Dispose();
            right.Dispose();
        }
    }

    private static BucketPlan EstablishRangeKnowledge(Subtree current, Span<PbtWriteOperation<TKey>> operations, BucketPlan plan, TrieUpdaterMetrics? metrics)
    {
        bool validatePrefixes = (current.IsEmpty || current.IsLeaf) && !plan.PrefixesValidated;
        if (!validatePrefixes && !plan.Precalculated.IsEmpty)
        {
            int mask = plan.Precalculated[0];
            int bound = BitOperations.IsPow2(mask) ? plan.Depth + PbtFourLevelGroupGeometry.LevelsPerGroup : plan.Depth;
            return plan.WithRangeKnowledge(Math.Max(plan.BranchDepth, bound), plan.PrefixesValidated);
        }
        if (!validatePrefixes) return plan.WithRangeKnowledge(Math.Max(plan.BranchDepth, plan.Depth), plan.PrefixesValidated);

        int branchDepth = FindOperationsBranchDepth(operations, plan.Depth, validatePrefixes, plan.IsSorted, metrics, out bool prefixesValidated);
        return plan.WithRangeKnowledge(branchDepth, plan.PrefixesValidated || prefixesValidated);
    }

    private static int FindBranchDepth(Subtree current, TKey firstKey, BucketPlan plan)
    {
        int branchDepth = plan.BranchDepth;
        if (!current.IsEmpty && current.IsLeaf)
        {
            TKey leafKey = current.Key;
            int difference = leafKey.FirstDifferingBit(firstKey, plan.Depth);
            branchDepth = Math.Min(branchDepth, difference);
        }
        else if (!current.IsEmpty)
        {
            int prefixStart = current.Path!.BitDepth;
            branchDepth = Math.Min(branchDepth, prefixStart + MatchingPrefixBits(current.Prefix, current.PrefixBitCount, firstKey, prefixStart));
        }
        return branchDepth;
    }

    private static int FindOperationsBranchDepth(Span<PbtWriteOperation<TKey>> operations, int depth, bool validatePrefixes, bool isSorted, TrieUpdaterMetrics? metrics, out bool prefixesValidated)
    {
        prefixesValidated = false;
        if (operations.IsEmpty) return depth;
        TKey firstKey = operations[0].Key;
        int branchDepth = firstKey.BitLength;
        bool equalLengths = true;
        bool hasPrefix = false;
        if (isSorted && !validatePrefixes && operations.Length > 1)
        {
            metrics?.IncrementOperationPrefixComparisons();
            return firstKey.FirstDifferingBit(operations[^1].Key, depth);
        }
        for (int index = 1; index < operations.Length; index++)
        {
            TKey key = operations[index].Key;
            TKey reference = isSorted ? operations[index - 1].Key : firstKey;
            metrics?.IncrementOperationPrefixComparisons();
            int difference = reference.FirstDifferingBit(key, depth);
            hasPrefix |= difference == Math.Min(reference.BitLength, key.BitLength);
            branchDepth = Math.Min(branchDepth, difference);
            equalLengths &= key.BitLength == firstKey.BitLength;
        }

        // A single reference does not prove prefix freedom for a variable-length child subset.
        prefixesValidated = validatePrefixes && !hasPrefix && (isSorted || equalLengths || operations.Length <= 2);
        return branchDepth;
    }

    internal static void Dispose(Span<Subtree> subtrees)
    {
        foreach (ref Subtree subtree in subtrees) subtree.Dispose();
    }

    private static int PrefixBit(Subtree subtree, int bit) => bit < subtree.Path!.BitDepth
        ? (subtree.Path.Path[bit >> 3] >> (7 - (bit & 7))) & 1
        : GetBit(subtree.Prefix, bit - subtree.Path.BitDepth);

    private static TPath BoundaryPath(TPath groupKey, int slot, int level)
    {
        if (level == 0) return groupKey;
        int depth = groupKey.BitDepth + level;
        Span<byte> path = stackalloc byte[(depth + 7) >> 3];
        path.Clear();
        groupKey.Path.CopyTo(path);
        path[^1] |= (byte)((slot & (0xF << (4 - level))) << (4 - (groupKey.BitDepth & 4)));
        return TPath.Create(path, depth);
    }

    /// <summary>A boundary occupant or an unplaced result, retaining the original path of a compressed prefix.</summary>
    /// <remarks>Value copies are borrowed; only Move transfers ownership of the single lease.</remarks>
    internal struct Subtree : IDisposable
    {
        private RefCountingMemory? _lease;
        private readonly NodeKind _kind;
        private readonly ReadOnlyMemory<byte> _encoding;
        private readonly TKey _key;
        private readonly ValueHash256 _valueOrLeft;
        private readonly ValueHash256 _right;

        internal Subtree(RefCountingMemory lease, ReadOnlyMemory<byte> encoding, TPath path)
        {
            _lease = lease;
            _kind = NodeKind.Original;
            _encoding = encoding;
            Path = path;
        }

        internal Subtree(ValueHash256 hash, TPath path)
        {
            _kind = hash == default ? NodeKind.Empty : NodeKind.Reference;
            Path = path;
        }

        internal Subtree(PbtWriteOperation<TKey> operation, TPath? path = null)
        {
            _kind = NodeKind.Leaf;
            _key = operation.Key;
            _valueOrLeft = operation.Value;
            Path = path;
        }

        internal Subtree(TPath path, in ValueHash256 left, in ValueHash256 right)
        {
            _kind = NodeKind.Branch;
            _valueOrLeft = left;
            _right = right;
            Path = path;
        }

        private Subtree(RefCountingMemory? lease, NodeKind kind, ReadOnlyMemory<byte> encoding, TKey key,
            ValueHash256 valueOrLeft, ValueHash256 right, TPath? path)
        {
            _lease = lease;
            _kind = kind;
            _encoding = encoding;
            _key = key;
            _valueOrLeft = valueOrLeft;
            _right = right;
            Path = path;
        }

        internal static Subtree TakeFrom<TSourceKey, TSourcePath>(ref TrieUpdater<TSourceKey, TSourcePath>.Subtree source)
            where TSourceKey : struct, IPbtKey<TSourceKey>
            where TSourcePath : class, IPbtNodePath<TSourcePath>
        {
            TKey key = default;
            if (source.IsLeaf)
            {
                TSourceKey sourceKey = source.Key;
                key = TKey.Create(sourceKey.Bytes);
            }
            TPath? path = source.Path is { } sourcePath
                ? sourcePath as TPath ?? TPath.Create(sourcePath.Path, sourcePath.BitDepth)
                : null;
            Subtree result = new(source._lease, source._kind, source._encoding, key,
                source._valueOrLeft, source._right, path);
            source = default;
            return result;
        }

        private readonly PbtNodeReader Reader => new(_encoding.Span);
        internal readonly TPath? Path { get; }
        internal readonly bool IsEmpty => _kind == NodeKind.Empty;
        internal readonly bool IsReference => _kind == NodeKind.Reference;
        internal readonly bool IsLeaf => _kind == NodeKind.Leaf || (_kind == NodeKind.Original && Reader.IsLeaf);
        internal readonly TKey Key => _kind == NodeKind.Leaf ? _key : TKey.Create(Reader.Key);
        internal readonly ReadOnlySpan<byte> Prefix => _kind == NodeKind.Branch ? [] : Reader.Prefix;
        internal readonly int PrefixBitCount => _kind == NodeKind.Branch ? 0 : Reader.PrefixBitCount;
        internal readonly ValueHash256 LeftHash => _kind == NodeKind.Branch ? _valueOrLeft : Reader.LeftHash;
        internal readonly ValueHash256 RightHash => _kind == NodeKind.Branch ? _right : Reader.RightHash;

        internal readonly int EncodedLength(int depth) => IsLeaf
            ? (_kind == NodeKind.Leaf ? 3 + _key.Length + 32 : _encoding.Length)
            : 3 + PbtBitPrefix.ByteCount(Path!.BitDepth + PrefixBitCount - depth) + 64;

        internal readonly ValueHash256 Encode(Span<byte> encoding, int depth)
        {
            if (_kind == NodeKind.Original && (IsLeaf || depth == Path!.BitDepth))
            {
                _encoding.Span.CopyTo(encoding);
                return PbtNodeCodec.Hash(Reader);
            }
            if (IsLeaf)
            {
                PbtNodeCodec.EncodeLeaf(encoding, _key, _valueOrLeft.Bytes);
                return PbtNodeCodec.Hash(new PbtNodeReader(encoding));
            }

            // EIP-8297 promotion absorbs skipped path bits; the source anchor remains unchanged until placement.
            int pathDepth = Path!.BitDepth;
            int bitCount = pathDepth + PrefixBitCount - depth;
            PbtNodeCodec.CreateBranchEncoding(encoding, bitCount, LeftHash, RightHash);
            Span<byte> prefix = encoding.Slice(3, PbtBitPrefix.ByteCount(bitCount));
            int pathBits = Math.Max(0, pathDepth - depth);
            PbtBitPrefix.CopyBits(Path.Path, Math.Min(depth, pathDepth), pathBits, prefix, 0);
            int prefixOffset = Math.Max(0, depth - pathDepth);
            PbtBitPrefix.CopyBits(Prefix, prefixOffset, bitCount - pathBits, prefix, pathBits);
            return Blake3Hash.Hash(encoding);
        }

        internal static Subtree Move(ref Subtree source)
        {
            Subtree result = source;
            source = default;
            return result;
        }

        public void Dispose()
        {
            RefCountingMemory? lease = _lease;
            this = default;
            ((IDisposable?)lease)?.Dispose();
        }
    }

    private static int MatchingPrefixBits(ReadOnlySpan<byte> prefixBytes, int prefixBitCount, TKey key, int keyOffset)
    {
        int available = key.BitLength - keyOffset;
        int count = Math.Min(prefixBitCount, available);
        ReadOnlySpan<byte> keyBytes = key.Bytes;
        int keyBitOffset = keyOffset & 7;
        int index = 0;
        for (; index + 8 <= count; index += 8)
        {
            int keyByteIndex = (keyOffset + index) >> 3;
            int keyByte = keyBitOffset == 0
                ? keyBytes[keyByteIndex]
                : ((keyBytes[keyByteIndex] << 8) | keyBytes[keyByteIndex + 1]) >> (8 - keyBitOffset);
            int difference = prefixBytes[index >> 3] ^ (keyByte & 0xFF);
            if (difference != 0) return index + BitOperations.LeadingZeroCount((uint)difference) - 24;
        }
        while (index < count && GetBit(prefixBytes, index) == key.GetBit(keyOffset + index)) index++;
        return index;
    }

    private readonly struct GroupFrameReader : IDisposable
    {
        private readonly RefCountingMemory? _lease;
        private readonly OffsetBuffer _offsets;
        private readonly LengthBuffer _lengths;

        internal GroupFrameReader(IPbtStore store, TPath groupKey, TrieUpdaterMetrics? metrics)
        {
            GroupKey = groupKey;
            metrics?.IncrementPhysicalGroupFetches();
            _lease = store.GetNodeGroup(groupKey);
            if (_lease is null) return;
            try
            {
                metrics?.IncrementGroupParses();
                PbtNodeGroupReader reader = new(groupKey, _lease.GetSpan());
                for (int position = 0; position < PbtNodeGroupCodec.PositionCount; position++)
                {
                    if (position == PbtFourLevelGroupGeometry.RootPosition && groupKey.BitDepth != 0) continue;
                    if (!reader.TryGetNodeRange(position, out int offset, out int length)) continue;
                    _offsets[position] = offset;
                    _lengths[position] = length;
                }
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        internal TPath GroupKey { get; }
        internal int BitDepth => GroupKey.BitDepth;

        internal ReadOnlyMemory<byte> GetEncoding(int position) => _lengths[position] == 0
            ? default
            : _lease!.Memory.Slice(_offsets[position], _lengths[position]);

        internal int CopyRange(PbtNodeGroupWriter writer, int startPosition, int endPosition)
        {
            while (startPosition < endPosition && _lengths[startPosition] == 0) startPosition++;
            if (startPosition == endPosition) return 0;
            int lastPosition = endPosition - 1;
            while (_lengths[lastPosition] == 0) lastPosition--;
            int startOffset = _offsets[startPosition];
            ReadOnlySpan<byte> entries = _lease!.GetSpan().Slice(startOffset,
                _offsets[lastPosition] + _lengths[lastPosition] - startOffset);
            return writer.CopyRange(entries, _offsets, _lengths, startPosition, lastPosition);
        }

        internal Subtree Acquire(int position, TPath path)
        {
            ReadOnlyMemory<byte> encoding = GetEncoding(position);
            if (encoding.IsEmpty) return default;
            _lease!.AcquireLease();
            try { return new(_lease, encoding, path); }
            catch
            {
                ((IDisposable)_lease).Dispose();
                throw;
            }
        }

        internal int Position(TPath path)
        {
            int completeBytes = BitDepth >> 3;
            int remainingBits = BitDepth & 7;
            if (PbtFourLevelGroupGeometry.GroupDepthOf(path.BitDepth) != BitDepth
                || !path.Path[..completeBytes].SequenceEqual(GroupKey.Path[..completeBytes])
                || (remainingBits != 0 && ((path.Path[completeBytes] ^ GroupKey.Path[completeBytes]) & 0xF0) != 0))
                throw new InvalidOperationException("The PBT node does not belong to the active group.");
            return PbtFourLevelGroupGeometry.PositionOf(path);
        }

        public void Dispose() => ((IDisposable?)_lease)?.Dispose();

        [InlineArray(PbtNodeGroupCodec.PositionCount)]
        private struct OffsetBuffer
        {
            private int _element;
        }

        [InlineArray(PbtNodeGroupCodec.PositionCount)]
        private struct LengthBuffer
        {
            private int _element;
        }
    }

    internal sealed class GroupMutationFrame : IDisposable
    {
        private readonly IPbtStore _store;
        private readonly TrieUpdaterMetrics? _metrics;
        private readonly GroupFrameReader _reader;
        private readonly PbtNodeGroupWriter _writer;
        private uint _taken;
        private int _nextPosition;
        private int _changedNodes;

        internal GroupMutationFrame(IPbtStore store, TPath groupKey, TrieUpdaterMetrics? metrics, IRefCountingMemoryProvider? memoryProvider)
        {
            MemoryProvider = memoryProvider ?? PooledRefCountingMemoryProvider.Instance;
            _store = store;
            _metrics = metrics;
            metrics?.IncrementGroupFrameResolutions();
            _reader = new(store, groupKey, metrics);
            _writer = new(groupKey, MemoryProvider);
        }

        internal IRefCountingMemoryProvider MemoryProvider { get; }
        internal TPath GroupKey => _reader.GroupKey;
        internal int BitDepth => _reader.BitDepth;

        internal Subtree Take(TPath path, bool allowAbsent = false)
        {
            int position = _reader.Position(path);
            if (position < _nextPosition) throw new InvalidOperationException("Cannot take a PBT node after its output position has passed.");
            Subtree node = (_taken & (1U << position)) == 0
                ? _reader.Acquire(position, path)
                : default;
            if (node.IsEmpty && !allowAbsent) throw new InvalidDataException("A referenced PBT node is missing.");
            _taken |= 1U << position;
            return node;
        }

        internal void Resolve(ref Subtree subtree)
        {
            if (subtree.IsReference)
                subtree = Take(subtree.Path!);
        }

        internal ValueHash256 Write(int position, int depth, ref Subtree node)
        {
            if (node.IsEmpty) return default;
            Resolve(ref node);
            if (position < _nextPosition) throw new InvalidOperationException("PBT nodes must be placed in increasing position order.");
            CopyUntouchedBefore(position);
            Span<byte> encoding = _writer.GetSpan(position, node.EncodedLength(depth));
            ValueHash256 hash = node.Encode(encoding, depth);
            if (!_reader.GetEncoding(position).Span.SequenceEqual(encoding)) _changedNodes++;
            _writer.Commit();
            _nextPosition = position + 1;
            node.Dispose();
            return hash;
        }

        internal bool TryCopyUnchangedSubtree(NodeGroupPath path, out Subtree root)
        {
            int position = path.Position;
            root = default;
            // Only a consumed source root proves this range belongs to the subtree being recomposed.
            if ((_taken & (1U << position)) == 0 || _reader.GetEncoding(position).IsEmpty) return false;
            int startPosition = position - 2 * path.Width + 2;
            CopyUntouchedBefore(startPosition);
            int copiedNodes = _reader.CopyRange(_writer, startPosition, position);
            if (copiedNodes != 0) _metrics?.AddBulkCopy(copiedNodes);
            _nextPosition = position;
            // Placement may promote the root and extend its compressed prefix, so copy only its descendants.
            root = _reader.Acquire(position, BoundaryPath(GroupKey, path.Slot, path.Length));
            return true;
        }

        private void CopyUntouchedBefore(int endPosition)
        {
            while (_nextPosition < endPosition)
            {
                int position = _nextPosition++;
                ReadOnlySpan<byte> previous = _reader.GetEncoding(position).Span;
                if (previous.IsEmpty) continue;
                if ((_taken & (1U << position)) != 0) _changedNodes++;
                else _writer.Write(position, previous);
            }
        }

        internal void Flush()
        {
            if (_taken == 0 && _writer.Availability == 0) return;
            CopyUntouchedBefore(PbtNodeGroupCodec.PositionCount);
            if (_changedNodes == 0) return;

            using RefCountingMemory? payload = _writer.Detach();
            _store.SetNodeGroup(GroupKey, payload);
            _metrics?.AddEmittedNodeWrites(_changedNodes);
        }

        public void Dispose()
        {
            _writer.Dispose();
            _reader.Dispose();
        }

    }
}

internal sealed class TrieUpdaterMetrics
{
    internal int PrecalculatedLevels { get; private set; }
    internal int SortedLevels { get; private set; }
    internal int FullKeySorts { get; private set; }
    internal int RadixPartitions { get; private set; }
    internal int OperationPrefixComparisons { get; private set; }
    internal int SynthesizedSingleBuckets { get; private set; }
    internal int PhysicalGroupFetches { get; private set; }
    internal int GroupParses { get; private set; }
    internal int GroupFrameResolutions { get; private set; }
    internal int EmittedNodeWrites { get; private set; }
    internal int BulkCopiedNodes { get; private set; }
    internal int BulkCopyOperations { get; private set; }

    internal void Add(TrieUpdaterMetrics metrics)
    {
        PrecalculatedLevels += metrics.PrecalculatedLevels;
        SortedLevels += metrics.SortedLevels;
        FullKeySorts += metrics.FullKeySorts;
        RadixPartitions += metrics.RadixPartitions;
        OperationPrefixComparisons += metrics.OperationPrefixComparisons;
        SynthesizedSingleBuckets += metrics.SynthesizedSingleBuckets;
        PhysicalGroupFetches += metrics.PhysicalGroupFetches;
        GroupParses += metrics.GroupParses;
        GroupFrameResolutions += metrics.GroupFrameResolutions;
        EmittedNodeWrites += metrics.EmittedNodeWrites;
        BulkCopiedNodes += metrics.BulkCopiedNodes;
        BulkCopyOperations += metrics.BulkCopyOperations;
    }

    internal void AddBulkCopy(int nodes)
    {
        BulkCopiedNodes += nodes;
        BulkCopyOperations++;
    }
    internal void IncrementPrecalculatedLevels() => PrecalculatedLevels++;
    internal void IncrementSortedLevels() => SortedLevels++;
    internal void IncrementFullKeySorts() => FullKeySorts++;
    internal void IncrementRadixPartitions() => RadixPartitions++;
    internal void IncrementOperationPrefixComparisons() => OperationPrefixComparisons++;
    internal void IncrementSynthesizedSingleBuckets() => SynthesizedSingleBuckets++;
    internal void IncrementPhysicalGroupFetches() => PhysicalGroupFetches++;
    internal void IncrementGroupParses() => GroupParses++;
    internal void IncrementGroupFrameResolutions() => GroupFrameResolutions++;
    internal void AddEmittedNodeWrites(int count) => EmittedNodeWrites += count;
}
