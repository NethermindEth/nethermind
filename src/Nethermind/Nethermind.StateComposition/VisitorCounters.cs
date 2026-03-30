// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Immutable;
using Nethermind.Core.Crypto;

namespace Nethermind.StateComposition;

/// <summary>
/// Mutable counters for ThreadLocal usage in StateCompositionVisitor.
/// Each worker thread gets its own instance — no locking needed.
/// Aggregate via MergeFrom() after traversal completes.
/// Short=Extension, Full=Branch, Value=Leaf.
/// </summary>
public sealed class VisitorCounters
{
    /// <summary>
    /// Maximum trie depth tracked per-level. Depths beyond this are clamped.
    /// </summary>
    public const int MaxTrackedDepth = 16;

    private readonly int _topN;

    // --- Global counters ---
    public long AccountsTotal;
    public long ContractsTotal;
    public long ContractsWithStorage;
    public long StorageSlotsTotal;

    public long AccountNodeBytes;
    public long StorageNodeBytes;

    public long AccountFullNodes;
    public long AccountShortNodes;
    public long AccountValueNodes;

    public long StorageFullNodes;
    public long StorageShortNodes;
    public long StorageValueNodes;

    public long TotalBranchChildren;
    public long TotalBranchNodes;

    public readonly DepthCounter[] AccountDepths = new DepthCounter[MaxTrackedDepth];
    public readonly DepthCounter[] StorageDepths = new DepthCounter[MaxTrackedDepth];

    // --- Per-contract storage trie tracking ---
    public readonly long[] StorageMaxDepthHistogram = new long[MaxTrackedDepth];

    // Current storage trie accumulator (reset per contract)
    private ValueHash256 _currentStorageRoot;
    private ValueHash256 _currentOwner;
    private int _currentStorageMaxDepth;
    private long _currentStorageNodes;
    private long _currentStorageValueNodes;
    private long _currentStorageTotalSize;
    private bool _hasActiveStorageTrie;

    // Per-depth counters for current storage trie
    private readonly DepthCounter[] _currentStorageDepths = new DepthCounter[MaxTrackedDepth];

    // Top-N contract rankings
    public TopContractEntry[] TopByDepth;
    public TopContractEntry[] TopByNodes;
    public TopContractEntry[] TopByValueNodes;
    public int TopByDepthCount;
    public int TopByNodesCount;
    public int TopByValueNodesCount;

    public VisitorCounters(int topN = 20)
    {
        _topN = topN;
        TopByDepth = new TopContractEntry[topN];
        TopByNodes = new TopContractEntry[topN];
        TopByValueNodes = new TopContractEntry[topN];
    }

    /// <summary>
    /// Begin tracking a new storage trie. Finalizes the previous one if active.
    /// Called from VisitAccount when account.HasStorage is true.
    /// </summary>
    public void BeginStorageTrie(in ValueHash256 storageRoot, in ValueHash256 owner)
    {
        if (_hasActiveStorageTrie)
            FinalizeCurrentStorageTrie();

        _currentStorageRoot = storageRoot;
        _currentOwner = owner;
        _currentStorageMaxDepth = 0;
        _currentStorageNodes = 0;
        _currentStorageValueNodes = 0;
        _currentStorageTotalSize = 0;
        _hasActiveStorageTrie = true;

        // Reset per-depth counters for this storage trie
        Array.Clear(_currentStorageDepths);
    }

    /// <summary>
    /// Track a storage node visit for per-contract statistics including per-depth breakdown.
    /// </summary>
    public void TrackStorageNode(int depth, int byteSize, bool isLeaf, bool isBranch)
    {
        _currentStorageNodes++;
        _currentStorageTotalSize += byteSize;
        if (depth > _currentStorageMaxDepth)
            _currentStorageMaxDepth = depth;
        if (isLeaf)
            _currentStorageValueNodes++;

        // Per-depth tracking for Levels[16]
        int depthIdx = Math.Min(depth, MaxTrackedDepth - 1);
        if (isBranch)
            _currentStorageDepths[depthIdx].AddFullNode(byteSize);
        else if (isLeaf)
            _currentStorageDepths[depthIdx].AddValueNode(byteSize);
        else
            _currentStorageDepths[depthIdx].AddShortNode(byteSize);
    }

    /// <summary>
    /// Flush any active storage trie tracking. Must be called after traversal completes
    /// to finalize the last contract's storage trie.
    /// </summary>
    public void Flush()
    {
        if (_hasActiveStorageTrie)
            FinalizeCurrentStorageTrie();
    }

    private void FinalizeCurrentStorageTrie()
    {
        _hasActiveStorageTrie = false;

        // Update histogram: which depth bucket does this trie's max depth fall into?
        int depthBucket = Math.Min(_currentStorageMaxDepth, MaxTrackedDepth - 1);
        StorageMaxDepthHistogram[depthBucket]++;

        // Build per-depth Levels[16] and Summary from current storage depth counters
        ImmutableArray<TrieLevelStat>.Builder levelsBuilder = ImmutableArray.CreateBuilder<TrieLevelStat>(MaxTrackedDepth);
        long summaryShort = 0, summaryFull = 0, summaryValue = 0, summarySize = 0;

        for (int i = 0; i < MaxTrackedDepth; i++)
        {
            ref DepthCounter dc = ref _currentStorageDepths[i];
            levelsBuilder.Add(new TrieLevelStat
            {
                Depth = i,
                ShortNodeCount = dc.ShortNodes,
                FullNodeCount = dc.FullNodes,
                ValueNodeCount = dc.ValueNodes,
                TotalSize = dc.TotalSize,
            });
            summaryShort += dc.ShortNodes;
            summaryFull += dc.FullNodes;
            summaryValue += dc.ValueNodes;
            summarySize += dc.TotalSize;
        }

        TopContractEntry entry = new()
        {
            Owner = _currentOwner,
            StorageRoot = _currentStorageRoot,
            MaxDepth = _currentStorageMaxDepth,
            TotalNodes = _currentStorageNodes,
            ValueNodes = _currentStorageValueNodes,
            TotalSize = _currentStorageTotalSize,
            Levels = levelsBuilder.MoveToImmutable(),
            Summary = new TrieLevelStat
            {
                Depth = -1, // Summary has no specific depth
                ShortNodeCount = summaryShort,
                FullNodeCount = summaryFull,
                ValueNodeCount = summaryValue,
                TotalSize = summarySize,
            },
        };

        TryInsert(TopByDepth, ref TopByDepthCount, entry, CompareByDepth);
        TryInsert(TopByNodes, ref TopByNodesCount, entry, CompareByTotalNodes);
        TryInsert(TopByValueNodes, ref TopByValueNodesCount, entry, CompareByValueNodes);
    }

    // --- Deterministic multi-field comparators ---

    /// <summary>MaxDepth DESC → TotalNodes DESC → ValueNodes DESC → Owner ASC</summary>
    internal static int CompareByDepth(in TopContractEntry a, in TopContractEntry b)
    {
        int c = a.MaxDepth.CompareTo(b.MaxDepth);
        if (c != 0) return c;
        c = a.TotalNodes.CompareTo(b.TotalNodes);
        if (c != 0) return c;
        c = a.ValueNodes.CompareTo(b.ValueNodes);
        if (c != 0) return c;
        // Owner: ascending (smaller hash = higher rank), so negate
        return -a.Owner.CompareTo(b.Owner);
    }

    /// <summary>TotalNodes DESC → MaxDepth DESC → ValueNodes DESC → Owner ASC</summary>
    internal static int CompareByTotalNodes(in TopContractEntry a, in TopContractEntry b)
    {
        int c = a.TotalNodes.CompareTo(b.TotalNodes);
        if (c != 0) return c;
        c = a.MaxDepth.CompareTo(b.MaxDepth);
        if (c != 0) return c;
        c = a.ValueNodes.CompareTo(b.ValueNodes);
        if (c != 0) return c;
        return -a.Owner.CompareTo(b.Owner);
    }

    /// <summary>ValueNodes DESC → MaxDepth DESC → TotalNodes DESC → Owner ASC</summary>
    internal static int CompareByValueNodes(in TopContractEntry a, in TopContractEntry b)
    {
        int c = a.ValueNodes.CompareTo(b.ValueNodes);
        if (c != 0) return c;
        c = a.MaxDepth.CompareTo(b.MaxDepth);
        if (c != 0) return c;
        c = a.TotalNodes.CompareTo(b.TotalNodes);
        if (c != 0) return c;
        return -a.Owner.CompareTo(b.Owner);
    }

    private delegate int EntryComparer(in TopContractEntry a, in TopContractEntry b);

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

    public void MergeFrom(VisitorCounters other)
    {
        AccountsTotal += other.AccountsTotal;
        ContractsTotal += other.ContractsTotal;
        ContractsWithStorage += other.ContractsWithStorage;
        StorageSlotsTotal += other.StorageSlotsTotal;

        AccountNodeBytes += other.AccountNodeBytes;
        StorageNodeBytes += other.StorageNodeBytes;

        AccountFullNodes += other.AccountFullNodes;
        AccountShortNodes += other.AccountShortNodes;
        AccountValueNodes += other.AccountValueNodes;

        StorageFullNodes += other.StorageFullNodes;
        StorageShortNodes += other.StorageShortNodes;
        StorageValueNodes += other.StorageValueNodes;

        TotalBranchChildren += other.TotalBranchChildren;
        TotalBranchNodes += other.TotalBranchNodes;

        for (int i = 0; i < MaxTrackedDepth; i++)
        {
            AccountDepths[i].FullNodes += other.AccountDepths[i].FullNodes;
            AccountDepths[i].ShortNodes += other.AccountDepths[i].ShortNodes;
            AccountDepths[i].ValueNodes += other.AccountDepths[i].ValueNodes;
            AccountDepths[i].TotalSize += other.AccountDepths[i].TotalSize;

            StorageDepths[i].FullNodes += other.StorageDepths[i].FullNodes;
            StorageDepths[i].ShortNodes += other.StorageDepths[i].ShortNodes;
            StorageDepths[i].ValueNodes += other.StorageDepths[i].ValueNodes;
            StorageDepths[i].TotalSize += other.StorageDepths[i].TotalSize;

            StorageMaxDepthHistogram[i] += other.StorageMaxDepthHistogram[i];
        }

        MergeTopN(TopByDepth, ref TopByDepthCount, other.TopByDepth, other.TopByDepthCount, CompareByDepth);
        MergeTopN(TopByNodes, ref TopByNodesCount, other.TopByNodes, other.TopByNodesCount, CompareByTotalNodes);
        MergeTopN(TopByValueNodes, ref TopByValueNodesCount, other.TopByValueNodes, other.TopByValueNodesCount, CompareByValueNodes);
    }

    private void MergeTopN(TopContractEntry[] target, ref int targetCount,
        TopContractEntry[] source, int sourceCount, EntryComparer comparer)
    {
        for (int i = 0; i < sourceCount; i++)
            TryInsert(target, ref targetCount, source[i], comparer);
    }
}
