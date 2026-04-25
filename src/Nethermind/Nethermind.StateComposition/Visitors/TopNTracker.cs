// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Immutable;
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

    internal delegate int EntryComparer(in TopContractEntry a, in TopContractEntry b);

    public bool TryInsertDepth(in TopContractEntry entry) =>
        TryInsert(TopByDepth, ref TopByDepthCount, entry, CompareByDepth);

    public bool TryInsertNodes(in TopContractEntry entry) =>
        TryInsert(TopByNodes, ref TopByNodesCount, entry, CompareByTotalNodes);

    public bool TryInsertValueNodes(in TopContractEntry entry) =>
        TryInsert(TopByValueNodes, ref TopByValueNodesCount, entry, CompareByValueNodes);

    public bool TryInsertSize(in TopContractEntry entry) =>
        TryInsert(TopBySize, ref TopBySizeCount, entry, CompareBySize);

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
        MergeTopN(TopByDepth, ref TopByDepthCount, other.TopByDepth, other.TopByDepthCount, CompareByDepth);
        MergeTopN(TopByNodes, ref TopByNodesCount, other.TopByNodes, other.TopByNodesCount, CompareByTotalNodes);
        MergeTopN(TopByValueNodes, ref TopByValueNodesCount, other.TopByValueNodes, other.TopByValueNodesCount, CompareByValueNodes);
        MergeTopN(TopBySize, ref TopBySizeCount, other.TopBySize, other.TopBySizeCount, CompareBySize);
    }

    private void MergeTopN(TopContractEntry[] target, ref int targetCount,
        TopContractEntry[] source, int sourceCount, EntryComparer comparer)
    {
        for (int i = 0; i < sourceCount; i++)
            TryInsert(target, ref targetCount, source[i], comparer);
    }

    /// <summary>MaxDepth DESC → TotalNodes DESC → ValueNodes DESC → Owner bytes ASC</summary>
    internal static int CompareByDepth(in TopContractEntry a, in TopContractEntry b)
    {
        int c = a.MaxDepth.CompareTo(b.MaxDepth);
        if (c != 0) return c;
        c = a.TotalNodes.CompareTo(b.TotalNodes);
        if (c != 0) return c;
        c = a.ValueNodes.CompareTo(b.ValueNodes);
        if (c != 0) return c;
        return a.Owner.CompareTo(b.Owner);
    }

    /// <summary>TotalNodes DESC → MaxDepth DESC → ValueNodes DESC → Owner bytes ASC</summary>
    internal static int CompareByTotalNodes(in TopContractEntry a, in TopContractEntry b)
    {
        int c = a.TotalNodes.CompareTo(b.TotalNodes);
        if (c != 0) return c;
        c = a.MaxDepth.CompareTo(b.MaxDepth);
        if (c != 0) return c;
        c = a.ValueNodes.CompareTo(b.ValueNodes);
        if (c != 0) return c;
        return a.Owner.CompareTo(b.Owner);
    }

    /// <summary>ValueNodes DESC → MaxDepth DESC → TotalNodes DESC → Owner bytes ASC</summary>
    internal static int CompareByValueNodes(in TopContractEntry a, in TopContractEntry b)
    {
        int c = a.ValueNodes.CompareTo(b.ValueNodes);
        if (c != 0) return c;
        c = a.MaxDepth.CompareTo(b.MaxDepth);
        if (c != 0) return c;
        c = a.TotalNodes.CompareTo(b.TotalNodes);
        if (c != 0) return c;
        return a.Owner.CompareTo(b.Owner);
    }

    /// <summary>TotalSize DESC → TotalNodes DESC → MaxDepth DESC → Owner bytes ASC</summary>
    internal static int CompareBySize(in TopContractEntry a, in TopContractEntry b)
    {
        int c = a.TotalSize.CompareTo(b.TotalSize);
        if (c != 0) return c;
        c = a.TotalNodes.CompareTo(b.TotalNodes);
        if (c != 0) return c;
        c = a.MaxDepth.CompareTo(b.MaxDepth);
        if (c != 0) return c;
        return a.Owner.CompareTo(b.Owner);
    }

    private bool TryInsert(TopContractEntry[] heap, ref int count, in TopContractEntry entry, EntryComparer comparer)
    {
        if (count < topN)
        {
            heap[count++] = entry;
            return true;
        }

        int minIdx = 0;
        for (int i = 1; i < topN; i++)
        {
            if (comparer(heap[i], heap[minIdx]) < 0)
                minIdx = i;
        }

        if (comparer(entry, heap[minIdx]) > 0)
        {
            heap[minIdx] = entry;
            return true;
        }

        return false;
    }
}
