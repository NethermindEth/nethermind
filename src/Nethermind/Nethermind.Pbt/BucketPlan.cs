// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Numerics;
using System.Runtime.InteropServices;
using static Nethermind.Pbt.TrieUpdater;

namespace Nethermind.Pbt;

/// <summary>Range knowledge and producer buckets carried through one traversal frame.</summary>
internal readonly ref struct BucketPlan(ReadOnlySpan<int> precalculated, int knownCommonPrefixLength, bool isSorted, Span<byte> buffer = default)
{
    private readonly Span<byte> _buffer = buffer;

    internal BucketPlan WithBuffer(Span<byte> buffer) =>
        new(Precalculated, KnownCommonPrefixLength, IsSorted, buffer);

    internal ReadOnlySpan<int> Precalculated { get; } = precalculated;
    /// <summary>Gets the known lower bound, in bits, on the operation range's common-prefix length.</summary>
    internal int KnownCommonPrefixLength { get; } = knownCommonPrefixLength;
    internal bool IsSorted { get; } = isSorted;

    /// <summary>Gets the scratch buffer size in bytes required by this plan.</summary>
    internal int GetBufferSize(int operationCount, int bitDepth) => !Precalculated.IsEmpty
        ? 0
        : sizeof(int) * Math.Min(operationCount, KnownCommonPrefixLength >= bitDepth + PbtFourLevelGroupGeometry.LevelsPerGroup
            ? 1 : PbtFourLevelGroupGeometry.BoundarySlots);

    internal PartitionOutcome BucketSort<TKey>(Span<PbtWriteOperation<TKey>> operations, int bitDepth, TrieUpdaterMetrics? metrics) where TKey : struct, IPbtKey<TKey>
    {
        int branchDepth = Math.Max(KnownCommonPrefixLength, operations.Length == 1 ? operations[0].Key.BitLength : bitDepth);
        BucketPlan plan = WithRangeKnowledge(branchDepth);
        if (!Precalculated.IsEmpty)
        {
            metrics?.IncrementPrecalculatedLevels();
            int mask = Precalculated[0];
            int bound = BitOperations.IsPow2(mask) ? bitDepth + PbtFourLevelGroupGeometry.LevelsPerGroup : bitDepth;
            return new(mask, Precalculated.Slice(1, BitOperations.PopCount((uint)mask)), plan.WithRangeKnowledge(Math.Max(branchDepth, bound)));
        }

        Span<int> counts = MemoryMarshal.Cast<byte, int>(_buffer);
        if (!operations.IsEmpty && branchDepth >= bitDepth + PbtFourLevelGroupGeometry.LevelsPerGroup)
        {
            metrics?.IncrementSynthesizedSingleBuckets();
            int slot = BoundarySlot(operations[0].Key, bitDepth);
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
            plan = new(default, branchDepth, true, _buffer);
        }
        else
        {
            metrics?.IncrementRadixPartitions();
            int mask = BucketizeLarge(operations, bitDepth, counts, metrics, out branchDepth);
            return new(mask, counts[..BitOperations.PopCount((uint)mask)], plan.WithRangeKnowledge(branchDepth));
        }

        int usedMask = 0;
        int countIndex = -1;
        int previousSlot = -1;
        foreach (PbtWriteOperation<TKey> operation in operations)
        {
            int slot = BoundarySlot(operation.Key, bitDepth);
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
            branchDepth = operations[0].Key.FirstDifferingBit(operations[^1].Key, bitDepth);
        }
        return new(usedMask, counts[..(countIndex + 1)], plan.WithRangeKnowledge(branchDepth));
    }

    internal BucketPlan WithRangeKnowledge(int knownCommonPrefixLength) =>
        new(Precalculated, knownCommonPrefixLength, IsSorted, _buffer);

    internal BucketPlan ForChild() =>
        new(default, KnownCommonPrefixLength, IsSorted);

    internal BucketPlan AfterFiltering(bool preservesOrder) => new(default, KnownCommonPrefixLength, IsSorted && preservesOrder);

    private const int FullSortThreshold = PbtFourLevelGroupGeometry.BoundarySlots;

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
}
