// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Numerics;
using System.Runtime.CompilerServices;
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
    internal readonly ref struct BucketPlan(ReadOnlySpan<int> precalculated, int depth, int branchDepth, bool isSorted, bool prefixesValidated)
    {
        internal ReadOnlySpan<int> Precalculated { get; } = precalculated;
        internal int Depth { get; } = depth;
        internal int BranchDepth { get; } = branchDepth;
        internal bool IsSorted { get; } = isSorted;
        internal bool PrefixesValidated { get; } = prefixesValidated;

        internal PartitionOutcome BucketSort<TKey>(Span<PbtWriteOperation<TKey>> operations, scoped Span<int> offsets, TrieUpdaterMetrics? metrics) where TKey : struct, IPbtKey<TKey>
        {
            if (!Precalculated.IsEmpty)
            {
                metrics?.IncrementPrecalculatedLevels();
                int mask = Precalculated[0];
                int countIndex = 1;
                offsets[0] = 0;
                for (int slot = 0; slot < PbtFourLevelGroupGeometry.BoundarySlots; slot++)
                    offsets[slot + 1] = offsets[slot] + ((mask & (1 << slot)) != 0 ? Precalculated[countIndex++] : 0);
                int bound = BitOperations.IsPow2(mask) ? Depth + PbtFourLevelGroupGeometry.LevelsPerGroup : Depth;
                return new(mask, WithRangeKnowledge(Math.Max(BranchDepth, bound), PrefixesValidated));
            }

            int branchDepth = operations.Length == 1 ? operations[0].Key.BitLength : BranchDepth;
            branchDepth = Math.Max(branchDepth, Depth);
            BucketPlan plan = WithRangeKnowledge(branchDepth, PrefixesValidated);
            if (!operations.IsEmpty && branchDepth >= Depth + PbtFourLevelGroupGeometry.LevelsPerGroup)
            {
                metrics?.IncrementSynthesizedSingleBuckets();
                int slot = BoundarySlot(operations[0].Key, Depth);
                offsets[..(slot + 1)].Clear();
                offsets[(slot + 1)..(PbtFourLevelGroupGeometry.BoundarySlots + 1)].Fill(operations.Length);
                return new(1 << slot, plan);
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
                plan = new(default, Depth, branchDepth, true, PrefixesValidated);
            }
            else
            {
                metrics?.IncrementRadixPartitions();
                int mask = BucketizeLarge(operations, Depth, offsets, metrics, out branchDepth);
                return new(mask, plan.WithRangeKnowledge(branchDepth, PrefixesValidated));
            }

            offsets[..(PbtFourLevelGroupGeometry.BoundarySlots + 1)].Clear();
            int usedMask = 0;
            foreach (PbtWriteOperation<TKey> operation in operations)
            {
                int slot = BoundarySlot(operation.Key, Depth);
                offsets[slot + 1]++;
                usedMask |= 1 << slot;
            }
            for (int slot = 0; slot < PbtFourLevelGroupGeometry.BoundarySlots; slot++)
                offsets[slot + 1] += offsets[slot];
            if (BitOperations.IsPow2(usedMask))
            {
                metrics?.IncrementOperationPrefixComparisons();
                branchDepth = operations[0].Key.FirstDifferingBit(operations[^1].Key, Depth);
            }
            return new(usedMask, plan.WithRangeKnowledge(branchDepth, PrefixesValidated));
        }

        internal BucketPlan WithRangeKnowledge(int branchDepth, bool prefixesValidated) =>
            new(Precalculated, Depth, branchDepth, IsSorted, prefixesValidated);

        internal BucketPlan ForChild(int slot)
        {
            int offset = Precalculated.IsEmpty ? 0 : Precalculated[17 + slot];
            return new(offset == 0 ? default : Precalculated[offset..], Depth + PbtFourLevelGroupGeometry.LevelsPerGroup,
                BranchDepth, IsSorted, PrefixesValidated);
        }

        internal BucketPlan AfterJump(int depth) => new(default, depth, BranchDepth, IsSorted, PrefixesValidated);

        internal BucketPlan AfterFiltering(bool preservesOrder) => new(default, Depth, BranchDepth, IsSorted && preservesOrder, PrefixesValidated);
    }

    /// <summary>The touched buckets and range knowledge established by partitioning.</summary>
    internal readonly ref struct PartitionOutcome(int usedMask, BucketPlan plan)
    {
        internal int UsedMask { get; } = usedMask;
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

    private static int BucketizeLarge<TKey>(Span<PbtWriteOperation<TKey>> operations, int groupDepth, Span<int> offsets, TrieUpdaterMetrics? metrics, out int branchDepth) where TKey : struct, IPbtKey<TKey>
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
        offsets[0] = 0;
        for (int bucket = 0; bucket < PbtFourLevelGroupGeometry.BoundarySlots; bucket++)
            offsets[bucket + 1] = offsets[bucket] + counts[bucket];
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
        if (changes.ShardNibbleIndex == 0)
            return UpdateRoot(store, currentRoot, operations.AsSpan(), plan, metrics, memoryProvider);

        int deleteCount = 0;
        for (int index = 0; index < operations.Count; index++)
        {
            if (operations[index].Kind != PbtWriteOperationKind.Delete) continue;
            (operations[deleteCount], operations[index]) = (operations[index], operations[deleteCount]);
            deleteCount++;
        }
        return UpdateRoot(store, currentRoot, operations.AsSpan(), default, metrics, memoryProvider);
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
            ValueHash256 hash = Place(group, ref result, PbtFourLevelGroupGeometry.RootPosition, 0);
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
        Resolve(ownerGroup, ref current);
        if (operations.IsEmpty) return Subtree.Move(ref current);

        if (current.IsEmpty || current.IsLeaf)
        {
            bool hasLeaf = !current.IsEmpty;
            TKey leafKey = hasLeaf ? current.Key : default;
            int setCount = 0;
            for (int index = 0; index < operations.Length; index++)
            {
                PbtWriteOperation<TKey> operation = operations[index];
                if (hasLeaf && operation.Key.Equals(leafKey))
                {
                    if (operation.Kind == PbtWriteOperationKind.Delete)
                    {
                        current.Dispose();
                    }
                    else
                    {
                        Subtree replacement = new(operation, current.Path);
                        current.Dispose();
                        current = Subtree.Move(ref replacement);
                    }
                }
                else if (operation.Kind == PbtWriteOperationKind.Set)
                {
                    (operations[setCount], operations[index]) = (operations[index], operations[setCount]);
                    setCount++;
                }
            }

            if (setCount != operations.Length) plan = plan.AfterFiltering(preservesOrder: true);
            operations = operations[..setCount];
            if (operations.IsEmpty) return Subtree.Move(ref current);
            if (current.IsEmpty && operations.Length == 1)
                return new Subtree(operations[0]);
        }
        else
        {
            // A shorter replacement can become valid when this same batch deletes the entire subtree.
            PbtWriteOperation<TKey>? terminalSet = null;
            int remainingCount = 0;
            for (int index = 0; index < operations.Length; index++)
            {
                PbtWriteOperation<TKey> operation = operations[index];
                if (operation.Key.BitLength == depth)
                {
                    if (operation.Kind == PbtWriteOperationKind.Set) terminalSet = operation;
                    continue;
                }
                (operations[remainingCount], operations[index]) = (operations[index], operations[remainingCount]);
                remainingCount++;
            }
            if (remainingCount != operations.Length)
            {
                Subtree remaining = FoldMutations(store, metrics, ownerGroup, ref current, operations[..remainingCount], plan.AfterFiltering(preservesOrder: true));
                try
                {
                    if (terminalSet is not { } replacement) return Subtree.Move(ref remaining);
                    if (!remaining.IsEmpty) throw new ArgumentException("Tree keys must be prefix-free.", nameof(operations));
                    return new Subtree(replacement);
                }
                finally { remaining.Dispose(); }
            }
        }

        plan = EstablishRangeKnowledge(current, operations, plan, metrics);
        Span<int> offsets = stackalloc int[PbtFourLevelGroupGeometry.BoundarySlots + 1];
        bool hasComputedPartition = false;
        PartitionOutcome partition = default;
        if (plan.Precalculated.IsEmpty && plan.BranchDepth <= depth)
        {
            partition = plan.BucketSort(operations, offsets, metrics);
            plan = partition.Plan;
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
                    plan.ForChild(BitOperations.TrailingZeroCount(plan.Precalculated[0])));
            }

            // The range's prefix survives the jump; the existing subtree only limits how far we can jump.
            return FoldMutations(store, metrics, ownerGroup, ref current, operations, plan.AfterJump(groupDepth));
        }

        if (ownerGroup.BitDepth == depth)
            return hasComputedPartition
                ? FoldBoundaryFromPartition(store, metrics, ownerGroup, ref current, operations, offsets, partition)
                : FoldBoundary(store, metrics, ownerGroup, ref current, operations, plan);

        using GroupMutationFrame group = new(store, PbtPathOperations.FromKey<TPath>(operations[0].Key.Bytes, depth), metrics, ownerGroup.MemoryProvider);
        Subtree result = hasComputedPartition
            ? FoldBoundaryFromPartition(store, metrics, group, ref current, operations, offsets, partition)
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
        Span<int> offsets = stackalloc int[PbtFourLevelGroupGeometry.BoundarySlots + 1];
        PartitionOutcome partition = plan.BucketSort(operations, offsets, metrics);
        return FoldBoundaryFromPartition(store, metrics, group, ref current, operations, offsets, partition);
    }

    private static Subtree FoldBoundaryFromPartition(
        IPbtStore store,
        TrieUpdaterMetrics? metrics,
        GroupMutationFrame group,
        ref Subtree current,
        Span<PbtWriteOperation<TKey>> operations,
        ReadOnlySpan<int> offsets,
        PartitionOutcome partition)
    {
        int depth = partition.Plan.Depth;
        RefList16<Subtree> boundaryBuffer = new(PbtFourLevelGroupGeometry.BoundarySlots);
        Span<Subtree> boundaries = boundaryBuffer.AsSpan();
        try
        {
            Decompose(group, ref current, depth, boundaries);

            for (int mask = partition.UsedMask; mask != 0; mask &= mask - 1)
            {
                int slot = BitOperations.TrailingZeroCount(mask);
                Span<PbtWriteOperation<TKey>> bucket = operations[offsets[slot]..offsets[slot + 1]];
                boundaries[slot] = FoldMutations(
                    store, metrics, group, ref boundaries[slot], bucket, partition.Plan.ForChild(slot));
            }

            return Compose(group, boundaries);
        }
        finally { Dispose(boundaries); }
    }

    internal static Subtree Compose(GroupMutationFrame group, Span<Subtree> boundaries)
    {
        int occupied = 0;
        for (int slot = 0; slot < boundaries.Length; slot++)
        {
            // Consume original boundary positions before ordered emission can pass their old locations.
            Resolve(group, ref boundaries[slot]);
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
                    frame.LeftHash = Place(group, ref result, frame.Path.Position - frame.Path.Width, frame.BranchPath!.BitDepth + 1);
                    frame.Stage = ComposeStage.RightCompleted;
                    if (halfWidth == 1)
                        result = Subtree.Move(ref boundaries[frame.Path.Slot + 1]);
                    else
                        frames[frameCount++] = new(frame.Occupied >> halfWidth, frame.Path.Right);
                    continue;
                }
                if (frame.Stage == ComposeStage.RightCompleted)
                {
                    ValueHash256 rightHash = Place(group, ref result, frame.Path.Position - 1, frame.BranchPath!.BitDepth + 1);
                    result = new Subtree(frame.BranchPath, frame.LeftHash, rightHash);
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

                frame.BranchPath = BoundaryPath(group.GroupKey, frame.Path.Slot, frame.Path.Length);
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
        internal TPath? BranchPath;
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

        Resolve(group, ref current);
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
            if (difference == Math.Min(leafKey.BitLength, firstKey.BitLength))
                throw new ArgumentException("Tree keys must be prefix-free.", "operations");
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
            if (validatePrefixes && difference == Math.Min(reference.BitLength, key.BitLength))
                throw new ArgumentException("Tree keys must be prefix-free.", nameof(operations));
            branchDepth = Math.Min(branchDepth, difference);
            equalLengths &= key.BitLength == firstKey.BitLength;
        }

        // A single reference does not prove prefix freedom for a variable-length child subset.
        prefixesValidated = validatePrefixes && (isSorted || equalLengths || operations.Length <= 2);
        return branchDepth;
    }

    internal static void Resolve(GroupMutationFrame group, ref Subtree subtree)
    {
        if (subtree.IsReference)
            subtree = group.Take(subtree.Path!, hash: subtree.Hash);
    }

    internal static ValueHash256 Place(GroupMutationFrame group, ref Subtree subtree, int position, int depth)
    {
        if (subtree.IsEmpty) return default;
        Resolve(group, ref subtree);
        return group.Write(position, depth, ref subtree);
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

        internal Subtree(RefCountingMemory lease, ReadOnlyMemory<byte> encoding, TPath path, ValueHash256 hash = default)
        {
            _lease = lease;
            _kind = NodeKind.Original;
            _encoding = encoding;
            Path = path;
            Hash = hash != default ? hash : PbtNodeCodec.Hash(new PbtNodeReader(encoding.Span));
        }

        internal Subtree(ValueHash256 hash, TPath path)
        {
            _kind = hash == default ? NodeKind.Empty : NodeKind.Reference;
            Hash = hash;
            Path = path;
        }

        internal Subtree(PbtWriteOperation<TKey> operation, TPath? path = null)
        {
            _kind = NodeKind.Leaf;
            _key = operation.Key;
            _valueOrLeft = operation.Value;
            Path = path;
            Span<byte> preimage = stackalloc byte[1 + _key.Length + 32];
            preimage[0] = 0;
            _key.Bytes.CopyTo(preimage[1..]);
            _valueOrLeft.Bytes.CopyTo(preimage[(1 + _key.Length)..]);
            Hash = Blake3Hash.Hash(preimage);
        }

        internal Subtree(TPath path, in ValueHash256 left, in ValueHash256 right)
        {
            _kind = NodeKind.Branch;
            _valueOrLeft = left;
            _right = right;
            Path = path;
            Span<byte> encoding = stackalloc byte[3 + 64];
            PbtNodeCodec.CreateBranchEncoding(encoding, 0, left, right);
            Hash = Blake3Hash.Hash(encoding);
        }

        private Subtree(RefCountingMemory? lease, NodeKind kind, ReadOnlyMemory<byte> encoding, TKey key,
            ValueHash256 valueOrLeft, ValueHash256 right, TPath? path, ValueHash256 hash)
        {
            _lease = lease;
            _kind = kind;
            _encoding = encoding;
            _key = key;
            _valueOrLeft = valueOrLeft;
            _right = right;
            Path = path;
            Hash = hash;
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
                source._valueOrLeft, source._right, path, source.Hash);
            source = default;
            return result;
        }

        private readonly PbtNodeReader Reader => new(_encoding.Span);
        internal readonly TPath? Path { get; }
        internal readonly ValueHash256 Hash { get; }
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
                return Hash;
            }
            if (IsLeaf)
            {
                PbtNodeCodec.EncodeLeaf(encoding, _key, _valueOrLeft.Bytes);
                return Hash;
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
            return depth == pathDepth ? Hash : Blake3Hash.Hash(encoding);
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

        internal Subtree Acquire(int position, TPath path, ValueHash256 hash)
        {
            ReadOnlyMemory<byte> encoding = GetEncoding(position);
            if (encoding.IsEmpty) return default;
            _lease!.AcquireLease();
            try { return new(_lease, encoding, path, hash); }
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

        internal Subtree Take(TPath path, bool allowAbsent = false, ValueHash256 hash = default)
        {
            int position = _reader.Position(path);
            if (position < _nextPosition) throw new InvalidOperationException("Cannot take a PBT node after its output position has passed.");
            Subtree node = (_taken & (1U << position)) == 0
                ? _reader.Acquire(position, path, hash)
                : default;
            if (node.IsEmpty && !allowAbsent) throw new InvalidDataException("A referenced PBT node is missing.");
            _taken |= 1U << position;
            return node;
        }

        internal ValueHash256 Write(int position, int depth, ref Subtree node)
        {
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
