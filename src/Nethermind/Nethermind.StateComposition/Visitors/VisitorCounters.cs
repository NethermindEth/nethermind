// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using Nethermind.Core.Crypto;

using Nethermind.StateComposition.Data;

namespace Nethermind.StateComposition.Visitors;

internal sealed class VisitorCounters(int topN = 20)
{
    public const int MaxTrackedDepth = 16;

    public long AccountsTotal;
    public long ContractsTotal;
    public long ContractsWithStorage;
    public long StorageSlotsTotal;
    public long EmptyAccounts;

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

    /// <summary>
    /// Aggregate bytecode size attributed to this worker, deduplicated by codeHash
    /// at the visitor level (shared map across all workers). Each unique codeHash
    /// contributes its bytecode length exactly once across the full scan.
    /// </summary>
    public long CodeBytes;

    /// <summary>
    /// Log-bucketed per-contract slot-count histogram.
    /// Bucket = min(Length-1, floor(log2(slotCount+1))).
    /// Length is bound to <see cref="CumulativeTrieStats.SlotHistogramLength"/>
    /// so the producer and the snapshot decoder agree on wire length.
    /// Backed inline: <see cref="Long16.Length"/> must match <see cref="CumulativeTrieStats.SlotHistogramLength"/>.
    /// </summary>
    public Long16 SlotCountHistogram;

    public DepthCounter16 AccountDepths;
    public DepthCounter16 StorageDepths;

    /// <summary>Branch occupancy histogram: index i = count of account-trie branches with (i+1) children.</summary>
    public Long16 BranchOccupancyHistogram;

    public Long16 StorageMaxDepthHistogram;

    /// <summary>
    /// Per-contract (owner → slot count) map for every contract-with-storage finalized
    /// on this thread. Written in <see cref="FinalizeCurrentStorageTrie"/>, merged by
    /// <see cref="MergeFrom"/> via dictionary copy (each account is visited by exactly
    /// one worker thread, so no cross-thread deduplication is required).
    /// Feeds the state holder's incremental slot-count tracker.
    /// </summary>
    public readonly Dictionary<ValueHash256, long> SlotCountsByOwner = [];

    /// <summary>
    /// Per-code-hash reference count accumulated on this thread. Each account whose
    /// <c>HasCode</c> is true bumps the count for its code hash by one. Merged across
    /// threads in <see cref="MergeFrom"/> to produce the total refcount per hash.
    /// </summary>
    public readonly Dictionary<ValueHash256, int> CodeHashRefcounts = [];

    private ValueHash256 _currentStorageRoot;
    private ValueHash256 _currentOwner;
    private int _currentStorageMaxDepth;
    private long _currentStorageNodes;
    private long _currentStorageValueNodes;
    private long _currentStorageTotalSize;
    private bool _hasActiveStorageTrie;

    private DepthCounter16 _currentStorageDepths;

    private TrieLevelStat[]? _levelScratch;

    internal TopNTracker TopN { get; } = new(topN);

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

        _currentStorageDepths = default;
    }

    public void TrackStorageNode(int depth, int byteSize, bool isLeaf, bool isBranch)
    {
        _currentStorageNodes++;
        _currentStorageTotalSize += byteSize;
        if (depth > _currentStorageMaxDepth)
            _currentStorageMaxDepth = depth;
        if (isLeaf)
            _currentStorageValueNodes++;

        int depthIdx = Math.Min(depth, MaxTrackedDepth - 1);
        if (isBranch)
            _currentStorageDepths[depthIdx].AddFullNode(byteSize);
        else if (isLeaf)
            _currentStorageDepths[depthIdx].AddValueNode(byteSize);
        else
            _currentStorageDepths[depthIdx].AddShortNode(byteSize);
    }

    public void Flush()
    {
        if (_hasActiveStorageTrie)
            FinalizeCurrentStorageTrie();
    }

    private void FinalizeCurrentStorageTrie()
    {
        _hasActiveStorageTrie = false;

        // Geth counts the valueNode inside a shortNode as a separate depth level,
        // so a single-leaf storage trie has MaxDepth=1 in Geth (not 0). Apply +1.
        int gethMaxDepth = _currentStorageMaxDepth + 1;

        int depthBucket = Math.Min(gethMaxDepth, MaxTrackedDepth - 1);
        StorageMaxDepthHistogram[depthBucket]++;

        int slotBucket = ComputeSlotBucket(_currentStorageValueNodes);
        SlotCountHistogram[slotBucket]++;

        // Remember this contract's exact slot count so the state holder can adjust
        // the histogram incrementally when later diffs move it between buckets.
        if (_currentStorageValueNodes > 0)
            SlotCountsByOwner[_currentOwner] = _currentStorageValueNodes;

        _levelScratch ??= new TrieLevelStat[MaxTrackedDepth];

        TrieLevelStat summary = BuildCurrentStorageLevels(_levelScratch);

        TopContractEntry candidate = new()
        {
            Owner = _currentOwner,
            StorageRoot = _currentStorageRoot,
            MaxDepth = gethMaxDepth,
            TotalNodes = _currentStorageNodes + _currentStorageValueNodes,
            ValueNodes = _currentStorageValueNodes,
            TotalSize = _currentStorageTotalSize,
            // Levels intentionally left as default — populated only if the entry ranks.
            Summary = summary,
        };

        RankCurrentContract(candidate);
    }

    private TrieLevelStat BuildCurrentStorageLevels(TrieLevelStat[] scratch) =>
        LevelStatsBuilder.Fill(_currentStorageDepths[..], scratch);

    private void RankCurrentContract(TopContractEntry candidate)
    {
        bool inserted = false;
        inserted |= TopN.TryInsertDepth(candidate);
        inserted |= TopN.TryInsertNodes(candidate);
        inserted |= TopN.TryInsertValueNodes(candidate);
        inserted |= TopN.TryInsertSize(candidate);
        if (!inserted) return;

        TrieLevelStat[] frozenCopy = new TrieLevelStat[MaxTrackedDepth];
        Array.Copy(_levelScratch!, frozenCopy, MaxTrackedDepth);
        TopN.SetLevelsForOwner(candidate.Owner, ImmutableCollectionsMarshal.AsImmutableArray(frozenCopy));
    }

    public void MergeFrom(VisitorCounters other)
    {
        AccountsTotal += other.AccountsTotal;
        ContractsTotal += other.ContractsTotal;
        ContractsWithStorage += other.ContractsWithStorage;
        StorageSlotsTotal += other.StorageSlotsTotal;
        EmptyAccounts += other.EmptyAccounts;

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

        CodeBytes += other.CodeBytes;

        // MaxTrackedDepth, Long16.Length, and CumulativeTrieStats.SlotHistogramLength
        // are all 16 by construction — every inline-backed row has matching length,
        // so a single unrolled loop merges them all.
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
            BranchOccupancyHistogram[i] += other.BranchOccupancyHistogram[i];
            SlotCountHistogram[i] += other.SlotCountHistogram[i];
        }

        // Per-account slot counts: straight copy — each account is visited by
        // exactly one worker, so no owner can appear in two thread-local maps.
        foreach (KeyValuePair<ValueHash256, long> kvp in other.SlotCountsByOwner)
            SlotCountsByOwner[kvp.Key] = kvp.Value;

        // Code-hash refcounts: additive merge. Each account contributed one increment
        // on its own thread, so summing across threads yields the total refcount.
        foreach (KeyValuePair<ValueHash256, int> kvp in other.CodeHashRefcounts)
        {
            ref int slot = ref CollectionsMarshal.GetValueRefOrAddDefault(CodeHashRefcounts, kvp.Key, out _);
            slot += kvp.Value;
        }

        TopN.MergeFrom(other.TopN);
    }

    public static int ComputeSlotBucket(long slotCount)
    {
        if (slotCount <= 0) return 0;
        // BitOperations.Log2(n) == floor(log2(n)) for n >= 1.
        int log = BitOperations.Log2((ulong)(slotCount + 1));
        return Math.Min(CumulativeTrieStats.SlotHistogramLength - 1, log);
    }
}
