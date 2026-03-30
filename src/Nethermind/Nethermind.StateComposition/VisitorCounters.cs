// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;

namespace Nethermind.StateComposition;

/// <summary>
/// Mutable counters for ThreadLocal usage in StateCompositionVisitor.
/// Each worker thread gets its own instance — no locking needed.
/// Aggregate via MergeFrom() after traversal completes.
/// </summary>
public sealed class VisitorCounters
{
    /// <summary>
    /// Maximum trie depth tracked per-level. Depths beyond this are clamped.
    /// Ethereum account tries are 64 nibbles deep max; 16 buckets covers
    /// the meaningful range with room to spare.
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

    public long AccountBranches;
    public long AccountExtensions;
    public long AccountLeaves;

    public long StorageBranches;
    public long StorageExtensions;
    public long StorageLeaves;

    public long TotalBranchChildren;
    public long TotalBranchNodes;

    public readonly DepthCounter[] AccountDepths = new DepthCounter[MaxTrackedDepth];
    public readonly DepthCounter[] StorageDepths = new DepthCounter[MaxTrackedDepth];

    // --- Per-contract storage trie tracking (Geth parity) ---
    public readonly long[] StorageMaxDepthHistogram = new long[MaxTrackedDepth];

    // Current storage trie accumulator (reset per contract)
    private ValueHash256 _currentStorageRoot;
    private int _currentStorageMaxDepth;
    private long _currentStorageNodes;
    private long _currentStorageSlots;
    private long _currentStorageBytes;
    private bool _hasActiveStorageTrie;

    // Top-N contract rankings (Geth parity: TopByDepth, TopByTotalNodes, TopByValueNodes)
    public TopContractEntry[] TopByDepth;
    public TopContractEntry[] TopByNodes;
    public TopContractEntry[] TopBySlots;
    public int TopByDepthCount;
    public int TopByNodesCount;
    public int TopBySlotsCount;

    public VisitorCounters(int topN = 20)
    {
        _topN = topN;
        TopByDepth = new TopContractEntry[topN];
        TopByNodes = new TopContractEntry[topN];
        TopBySlots = new TopContractEntry[topN];
    }

    /// <summary>
    /// Begin tracking a new storage trie. Finalizes the previous one if active.
    /// Called from VisitAccount when account.HasStorage is true.
    /// </summary>
    public void BeginStorageTrie(in ValueHash256 storageRoot)
    {
        if (_hasActiveStorageTrie)
            FinalizeCurrentStorageTrie();

        _currentStorageRoot = storageRoot;
        _currentStorageMaxDepth = 0;
        _currentStorageNodes = 0;
        _currentStorageSlots = 0;
        _currentStorageBytes = 0;
        _hasActiveStorageTrie = true;
    }

    /// <summary>
    /// Track a storage node visit for per-contract statistics.
    /// </summary>
    public void TrackStorageNode(int depth, int byteSize, bool isLeaf)
    {
        _currentStorageNodes++;
        _currentStorageBytes += byteSize;
        if (depth > _currentStorageMaxDepth)
            _currentStorageMaxDepth = depth;
        if (isLeaf)
            _currentStorageSlots++;
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

        TopContractEntry entry = new()
        {
            StorageRoot = _currentStorageRoot,
            MaxDepth = _currentStorageMaxDepth,
            TotalNodes = _currentStorageNodes,
            StorageSlots = _currentStorageSlots,
            ByteSize = _currentStorageBytes,
        };

        TryInsert(TopByDepth, ref TopByDepthCount, entry, static e => e.MaxDepth);
        TryInsert(TopByNodes, ref TopByNodesCount, entry, static e => e.TotalNodes);
        TryInsert(TopBySlots, ref TopBySlotsCount, entry, static e => e.StorageSlots);
    }

    private void TryInsert(TopContractEntry[] heap, ref int count, TopContractEntry entry, Func<TopContractEntry, long> key)
    {
        if (count < _topN)
        {
            heap[count++] = entry;
            return;
        }

        int minIdx = 0;
        long minVal = key(heap[0]);
        for (int i = 1; i < _topN; i++)
        {
            long val = key(heap[i]);
            if (val < minVal)
            {
                minIdx = i;
                minVal = val;
            }
        }

        if (key(entry) > minVal)
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

        AccountBranches += other.AccountBranches;
        AccountExtensions += other.AccountExtensions;
        AccountLeaves += other.AccountLeaves;

        StorageBranches += other.StorageBranches;
        StorageExtensions += other.StorageExtensions;
        StorageLeaves += other.StorageLeaves;

        TotalBranchChildren += other.TotalBranchChildren;
        TotalBranchNodes += other.TotalBranchNodes;

        for (int i = 0; i < MaxTrackedDepth; i++)
        {
            AccountDepths[i].Branches += other.AccountDepths[i].Branches;
            AccountDepths[i].Extensions += other.AccountDepths[i].Extensions;
            AccountDepths[i].Leaves += other.AccountDepths[i].Leaves;
            AccountDepths[i].ByteSize += other.AccountDepths[i].ByteSize;

            StorageDepths[i].Branches += other.StorageDepths[i].Branches;
            StorageDepths[i].Extensions += other.StorageDepths[i].Extensions;
            StorageDepths[i].Leaves += other.StorageDepths[i].Leaves;
            StorageDepths[i].ByteSize += other.StorageDepths[i].ByteSize;

            StorageMaxDepthHistogram[i] += other.StorageMaxDepthHistogram[i];
        }

        MergeTopN(TopByDepth, ref TopByDepthCount, other.TopByDepth, other.TopByDepthCount, static e => e.MaxDepth);
        MergeTopN(TopByNodes, ref TopByNodesCount, other.TopByNodes, other.TopByNodesCount, static e => e.TotalNodes);
        MergeTopN(TopBySlots, ref TopBySlotsCount, other.TopBySlots, other.TopBySlotsCount, static e => e.StorageSlots);
    }

    private void MergeTopN(TopContractEntry[] target, ref int targetCount,
        TopContractEntry[] source, int sourceCount, Func<TopContractEntry, long> key)
    {
        for (int i = 0; i < sourceCount; i++)
            TryInsert(target, ref targetCount, source[i], key);
    }
}
