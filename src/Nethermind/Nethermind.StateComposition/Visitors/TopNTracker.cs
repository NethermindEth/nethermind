// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Nethermind.Core.Crypto;
using Nethermind.StateComposition.Data;

namespace Nethermind.StateComposition.Visitors;

internal sealed class TopNTracker(int topN)
{
    public readonly TopContractEntry[] TopByDepth = new TopContractEntry[topN];
    public readonly TopContractEntry[] TopByNodes = new TopContractEntry[topN];
    public readonly TopContractEntry[] TopByValueNodes = new TopContractEntry[topN];
    public readonly TopContractEntry[] TopBySize = new TopContractEntry[topN];
    public int TopByDepthCount;
    public int TopByNodesCount;
    public int TopByValueNodesCount;
    public int TopBySizeCount;

    internal interface IEntryComparer : IComparer<TopContractEntry>
    {
        int Compare(in TopContractEntry a, in TopContractEntry b);
    }

    /// <summary>Reverses the order of an inner strategy. Used to sort top-N display-first.</summary>
    internal readonly struct Reverse<TInner> : IComparer<TopContractEntry>
        where TInner : struct, IEntryComparer
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Compare(TopContractEntry a, TopContractEntry b) => default(TInner).Compare(in b, in a);
    }

    public bool TryInsertDepth(in TopContractEntry entry) =>
        TryInsert<ByDepth>(TopByDepth, ref TopByDepthCount, in entry);

    public bool TryInsertNodes(in TopContractEntry entry) =>
        TryInsert<ByTotalNodes>(TopByNodes, ref TopByNodesCount, in entry);

    public bool TryInsertValueNodes(in TopContractEntry entry) =>
        TryInsert<ByValueNodes>(TopByValueNodes, ref TopByValueNodesCount, in entry);

    public bool TryInsertSize(in TopContractEntry entry) =>
        TryInsert<BySize>(TopBySize, ref TopBySizeCount, in entry);

    public void SetLevelsForOwner(in ValueHash256 owner, ImmutableArray<TrieLevelStat> levels)
    {
        SetLevelsIn(TopByDepth, TopByDepthCount, owner, levels);
        SetLevelsIn(TopByNodes, TopByNodesCount, owner, levels);
        SetLevelsIn(TopByValueNodes, TopByValueNodesCount, owner, levels);
        SetLevelsIn(TopBySize, TopBySizeCount, owner, levels);
    }

    private static void SetLevelsIn(TopContractEntry[] heap, int count, in ValueHash256 owner, ImmutableArray<TrieLevelStat> levels)
    {
        for (int i = 0; i < count; i++)
        {
            if (heap[i].Owner.Equals(owner))
                heap[i] = heap[i] with { Levels = levels };
        }
    }

    public void MergeFrom(TopNTracker other)
    {
        MergeTopN<ByDepth>(TopByDepth, ref TopByDepthCount, other.TopByDepth, other.TopByDepthCount);
        MergeTopN<ByTotalNodes>(TopByNodes, ref TopByNodesCount, other.TopByNodes, other.TopByNodesCount);
        MergeTopN<ByValueNodes>(TopByValueNodes, ref TopByValueNodesCount, other.TopByValueNodes, other.TopByValueNodesCount);
        MergeTopN<BySize>(TopBySize, ref TopBySizeCount, other.TopBySize, other.TopBySizeCount);
    }

    private void MergeTopN<TComparer>(TopContractEntry[] target, ref int targetCount,
        TopContractEntry[] source, int sourceCount)
        where TComparer : struct, IEntryComparer
    {
        for (int i = 0; i < sourceCount; i++)
            TryInsert<TComparer>(target, ref targetCount, in source[i]);
    }

    /// <summary>MaxDepth DESC → TotalNodes DESC → ValueNodes DESC → Owner bytes ASC</summary>
    internal readonly struct ByDepth : IEntryComparer
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Compare(in TopContractEntry a, in TopContractEntry b)
        {
            int c = a.MaxDepth.CompareTo(b.MaxDepth);
            if (c != 0) return c;
            c = a.TotalNodes.CompareTo(b.TotalNodes);
            if (c != 0) return c;
            c = a.ValueNodes.CompareTo(b.ValueNodes);
            if (c != 0) return c;
            return a.Owner.CompareTo(b.Owner);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Compare(TopContractEntry a, TopContractEntry b) => Compare(in a, in b);
    }

    /// <summary>TotalNodes DESC → MaxDepth DESC → ValueNodes DESC → Owner bytes ASC</summary>
    internal readonly struct ByTotalNodes : IEntryComparer
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Compare(in TopContractEntry a, in TopContractEntry b)
        {
            int c = a.TotalNodes.CompareTo(b.TotalNodes);
            if (c != 0) return c;
            c = a.MaxDepth.CompareTo(b.MaxDepth);
            if (c != 0) return c;
            c = a.ValueNodes.CompareTo(b.ValueNodes);
            if (c != 0) return c;
            return a.Owner.CompareTo(b.Owner);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Compare(TopContractEntry a, TopContractEntry b) => Compare(in a, in b);
    }

    /// <summary>ValueNodes DESC → MaxDepth DESC → TotalNodes DESC → Owner bytes ASC</summary>
    internal readonly struct ByValueNodes : IEntryComparer
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Compare(in TopContractEntry a, in TopContractEntry b)
        {
            int c = a.ValueNodes.CompareTo(b.ValueNodes);
            if (c != 0) return c;
            c = a.MaxDepth.CompareTo(b.MaxDepth);
            if (c != 0) return c;
            c = a.TotalNodes.CompareTo(b.TotalNodes);
            if (c != 0) return c;
            return a.Owner.CompareTo(b.Owner);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Compare(TopContractEntry a, TopContractEntry b) => Compare(in a, in b);
    }

    /// <summary>TotalSize DESC → TotalNodes DESC → MaxDepth DESC → Owner bytes ASC</summary>
    internal readonly struct BySize : IEntryComparer
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Compare(in TopContractEntry a, in TopContractEntry b)
        {
            int c = a.TotalSize.CompareTo(b.TotalSize);
            if (c != 0) return c;
            c = a.TotalNodes.CompareTo(b.TotalNodes);
            if (c != 0) return c;
            c = a.MaxDepth.CompareTo(b.MaxDepth);
            if (c != 0) return c;
            return a.Owner.CompareTo(b.Owner);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Compare(TopContractEntry a, TopContractEntry b) => Compare(in a, in b);
    }

    private bool TryInsert<TComparer>(TopContractEntry[] heap, ref int count, in TopContractEntry entry)
        where TComparer : struct, IEntryComparer
    {
        if (count < topN)
        {
            heap[count++] = entry;
            return true;
        }

        TComparer comparer = default;
        int minIdx = 0;
        for (int i = 1; i < topN; i++)
        {
            if (comparer.Compare(in heap[i], in heap[minIdx]) < 0)
                minIdx = i;
        }

        if (comparer.Compare(in entry, in heap[minIdx]) > 0)
        {
            heap[minIdx] = entry;
            return true;
        }

        return false;
    }
}
