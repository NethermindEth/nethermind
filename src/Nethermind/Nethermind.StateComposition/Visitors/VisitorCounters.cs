// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Nethermind.Core.Crypto;

using Nethermind.StateComposition.Data;

namespace Nethermind.StateComposition.Visitors;

/// <summary>
/// Mutable counters for ThreadLocal usage in StateCompositionVisitor.
/// Each worker thread gets its own instance — no locking needed.
/// Aggregate via MergeFrom() after traversal completes.
/// Short=Extension+Leaf (matches Geth shortNode), Full=Branch, Value=Leaf.
/// </summary>
public sealed class VisitorCounters(int topN = 20)
{
    /// <summary>
    /// Maximum trie depth tracked per-level. Depths beyond this are clamped.
    /// </summary>
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
    /// Length is bound to <see cref="CumulativeSizeStats.SlotHistogramLength"/>
    /// so the producer and the snapshot decoder agree on wire length.
    /// </summary>
    public readonly long[] SlotCountHistogram = new long[CumulativeSizeStats.SlotHistogramLength];

    public readonly DepthCounter[] AccountDepths = new DepthCounter[MaxTrackedDepth];
    public readonly DepthCounter[] StorageDepths = new DepthCounter[MaxTrackedDepth];

    /// <summary>Branch occupancy histogram: index i = count of account-trie branches with (i+1) children.</summary>
    public readonly long[] BranchOccupancyHistogram = new long[MaxTrackedDepth];

    public readonly long[] StorageMaxDepthHistogram = new long[MaxTrackedDepth];

    private ValueHash256 _currentStorageRoot;
    private ValueHash256 _currentOwner;
    private int _currentStorageMaxDepth;
    private long _currentStorageNodes;
    private long _currentStorageValueNodes;
    private long _currentStorageTotalSize;
    private bool _hasActiveStorageTrie;

    private readonly DepthCounter[] _currentStorageDepths = new DepthCounter[MaxTrackedDepth];

    // Scratch array for building TrieLevelStat[] without allocating a Builder each time.
    // Allocated lazily on first contract-with-storage; reused across contracts.
    // Only copied to a fresh array (and frozen to ImmutableArray) when the contract ranks in Top-N.
    private TrieLevelStat[]? _levelScratch;

    internal TopNTracker TopN { get; } = new(topN);

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

        // Geth counts the valueNode inside a shortNode as a separate depth level,
        // so a single-leaf storage trie has MaxDepth=1 in Geth (not 0). Apply +1.
        int gethMaxDepth = _currentStorageMaxDepth + 1;

        // Update histogram using Geth-compatible depth
        int depthBucket = Math.Min(gethMaxDepth, MaxTrackedDepth - 1);
        StorageMaxDepthHistogram[depthBucket]++;

        // Log-bucketed slot-count histogram: bucket = min(15, floor(log2(slotCount + 1))).
        int slotBucket = ComputeSlotBucket(_currentStorageValueNodes);
        SlotCountHistogram[slotBucket]++;

        // Build per-depth Levels[16] summary into a reusable scratch array.
        // The scratch array is allocated once per VisitorCounters instance (lazily)
        // and reused across all contracts — no per-contract Builder/array allocation.
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

    /// <summary>
    /// Populate <paramref name="scratch"/> with the current contract's per-depth stats and
    /// return the rolled-up summary row. ValueNodes are depth-shifted by +1 to match Geth.
    /// </summary>
    private TrieLevelStat BuildCurrentStorageLevels(TrieLevelStat[] scratch)
    {
        long summaryShort = 0, summaryFull = 0, summaryValue = 0, summarySize = 0;

        for (int i = 0; i < MaxTrackedDepth; i++)
        {
            ref DepthCounter dc = ref _currentStorageDepths[i];
            // Geth counts valueNode at depth+1 from its leaf shortNode
            long shiftedValue = i > 0 ? _currentStorageDepths[i - 1].ValueNodes : 0;
            scratch[i] = new TrieLevelStat
            {
                Depth = i,
                ShortNodeCount = dc.ShortNodes + dc.ValueNodes,
                FullNodeCount = dc.FullNodes,
                ValueNodeCount = shiftedValue,
                TotalSize = dc.TotalSize,
            };
            summaryShort += dc.ShortNodes + dc.ValueNodes;
            summaryFull += dc.FullNodes;
            summaryValue += dc.ValueNodes; // Real total (not shifted)
            summarySize += dc.TotalSize;
        }

        return new TrieLevelStat
        {
            Depth = -1, // Summary has no specific depth
            ShortNodeCount = summaryShort,
            FullNodeCount = summaryFull,
            ValueNodeCount = summaryValue,
            TotalSize = summarySize,
        };
    }

    /// <summary>
    /// Attempt to insert the candidate into the Top-N ranking. Freezes Levels only when the
    /// contract ranks — most contracts (~99.99%) skip the freeze entirely.
    /// </summary>
    private void RankCurrentContract(TopContractEntry candidate)
    {
        if (!TopN.WouldInsert(candidate)) return;

        // Contract ranks: freeze a fresh copy of the scratch into ImmutableArray.
        // The scratch itself stays mutable for the next contract.
        TrieLevelStat[] frozenCopy = new TrieLevelStat[MaxTrackedDepth];
        Array.Copy(_levelScratch!, frozenCopy, MaxTrackedDepth);
        candidate = candidate with { Levels = ImmutableCollectionsMarshal.AsImmutableArray(frozenCopy) };
        TopN.Insert(candidate);
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
        }

        // Slot-count histogram length is bound to CumulativeSizeStats.SlotHistogramLength,
        // not MaxTrackedDepth, so it needs its own loop.
        for (int i = 0; i < SlotCountHistogram.Length; i++)
            SlotCountHistogram[i] += other.SlotCountHistogram[i];

        TopN.MergeFrom(other.TopN);
    }

    /// <summary>
    /// Compute the log2 bucket index for a given slot count.
    /// <c>bucket = min(SlotHistogramLength-1, floor(log2(slotCount + 1)))</c>.
    /// <para>slotCount = 0 maps to bucket 0.</para>
    /// </summary>
    public static int ComputeSlotBucket(long slotCount)
    {
        if (slotCount <= 0) return 0;
        // BitOperations.Log2(n) == floor(log2(n)) for n >= 1.
        int log = BitOperations.Log2((ulong)(slotCount + 1));
        return Math.Min(CumulativeSizeStats.SlotHistogramLength - 1, log);
    }
}
