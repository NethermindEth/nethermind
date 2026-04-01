// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.StateComposition;

/// <summary>
/// Manages Top-N contract rankings across 4 categories: by depth, by total nodes,
/// by value nodes, and by total size. Uses O(N) min-replacement insertion with
/// deterministic multi-field comparators matching Geth's sort order + Owner hash tiebreaker.
/// Extracted from VisitorCounters to satisfy SRP (H3).
/// </summary>
internal sealed class TopNTracker
{
    private readonly int _topN;

    public TopContractEntry[] TopByDepth;
    public TopContractEntry[] TopByNodes;
    public TopContractEntry[] TopByValueNodes;
    public TopContractEntry[] TopBySize;
    public int TopByDepthCount;
    public int TopByNodesCount;
    public int TopByValueNodesCount;
    public int TopBySizeCount;

    public TopNTracker(int topN)
    {
        _topN = topN;
        TopByDepth = new TopContractEntry[topN];
        TopByNodes = new TopContractEntry[topN];
        TopByValueNodes = new TopContractEntry[topN];
        TopBySize = new TopContractEntry[topN];
    }

    internal delegate int EntryComparer(in TopContractEntry a, in TopContractEntry b);

    /// <summary>
    /// Insert an entry into all 4 ranking categories.
    /// </summary>
    public void Insert(TopContractEntry entry)
    {
        TryInsert(TopByDepth, ref TopByDepthCount, entry, CompareByDepth);
        TryInsert(TopByNodes, ref TopByNodesCount, entry, CompareByTotalNodes);
        TryInsert(TopByValueNodes, ref TopByValueNodesCount, entry, CompareByValueNodes);
        TryInsert(TopBySize, ref TopBySizeCount, entry, CompareBySize);
    }

    /// <summary>
    /// Merge another tracker's entries into this one.
    /// </summary>
    public void MergeFrom(TopNTracker other)
    {
        MergeTopN(TopByDepth, ref TopByDepthCount, other.TopByDepth, other.TopByDepthCount, CompareByDepth);
        MergeTopN(TopByNodes, ref TopByNodesCount, other.TopByNodes, other.TopByNodesCount, CompareByTotalNodes);
        MergeTopN(TopByValueNodes, ref TopByValueNodesCount, other.TopByValueNodes, other.TopByValueNodesCount, CompareByValueNodes);
        MergeTopN(TopBySize, ref TopBySizeCount, other.TopBySize, other.TopBySizeCount, CompareBySize);
    }

    // --- Deterministic multi-field comparators ---
    // Each follows: primary metric DESC → secondary DESC → tertiary DESC → Owner bytes ASC (tiebreaker)
    // Owner ASC matches Geth's bytes.Compare(a.Owner[:], b.Owner[:])

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

    private void TryInsert(TopContractEntry[] heap, ref int count, TopContractEntry entry, EntryComparer comparer)
    {
        if (count < _topN)
        {
            heap[count++] = entry;
            return;
        }

        // Find the minimum entry in the heap
        int minIdx = 0;
        for (int i = 1; i < _topN; i++)
        {
            if (comparer(heap[i], heap[minIdx]) < 0)
                minIdx = i;
        }

        // Replace if new entry is greater than current minimum
        if (comparer(entry, heap[minIdx]) > 0)
            heap[minIdx] = entry;
    }

    private void MergeTopN(TopContractEntry[] target, ref int targetCount,
        TopContractEntry[] source, int sourceCount, EntryComparer comparer)
    {
        for (int i = 0; i < sourceCount; i++)
            TryInsert(target, ref targetCount, source[i], comparer);
    }
}
