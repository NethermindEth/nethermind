// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.StateComposition.Data;
using Nethermind.Trie;

namespace Nethermind.StateComposition.Visitors;

internal sealed class StateCompositionVisitor(
    ILogManager logManager,
    int topN = 20,
    bool excludeStorage = false,
    Func<ValueHash256, int>? codeSizeLookup = null,
    CancellationToken ct = default)
    : ITreeVisitor<StateCompositionContext>, IDisposable
{
    private readonly ILogger _logger = logManager.GetClassLogger<StateCompositionVisitor>();

    private readonly ThreadLocal<VisitorCounters> _localCounters = new(() => new VisitorCounters(topN), trackAllValues: true);

    private const int MaxDepthIndex = VisitorCounters.MaxTrackedDepth - 1;

    private readonly ConcurrentDictionary<ValueHash256, int> _codeHashSizes = new();

    private VisitorCounters? _aggregated;

    // Write-once latch (0→1); no interlock needed for a single-bit transition.
    private volatile bool _missingNodesObserved;

    public bool IsFullDbScan => true;
    public ReadFlags ExtraReadFlag => ReadFlags.HintCacheMiss;
    public bool ExpectAccounts => true;

    public bool MissingNodesObserved => _missingNodesObserved;

    public bool ShouldVisit(in StateCompositionContext ctx, in ValueHash256 nextNode)
    {
        if (ct.IsCancellationRequested)
            return false;

        // Track branch occupancy: ShouldVisit is called for each non-null child
        // of a branch node, with BranchChildIndex set to the child's position.
        // Only count account trie children to match TotalBranchNodes (account-only).
        if (ctx is { BranchChildIndex: not null, IsStorage: false })
            (ctx.Counters ?? _localCounters.Value!).TotalBranchChildren++;

        return true;
    }

    public void VisitTree(in StateCompositionContext ctx, in ValueHash256 rootHash)
    {
    }

    public void VisitMissingNode(in StateCompositionContext ctx, in ValueHash256 nodeHash)
    {
        // Counter increments every miss; log is latched to the first so a pruning
        // window eviction mid-scan cannot flood the log.
        bool wasFirst = !_missingNodesObserved;
        _missingNodesObserved = true;
        Metrics.IncrementScanMissingNodes();
        if (wasFirst && _logger.IsWarn)
            _logger.Warn($"StateComposition: missing node at depth {ctx.Level}, storage={ctx.IsStorage}");
    }

    public void VisitBranch(in StateCompositionContext ctx, TrieNode node)
    {
        VisitorCounters c = ctx.Counters ?? _localCounters.Value!;
        int byteSize = node.FullRlp.Length;
        int depth = Math.Min(ctx.Level, MaxDepthIndex);

        if (ctx.IsStorage)
        {
            c.StorageFullNodes++;
            c.StorageNodeBytes += byteSize;
            c.StorageDepths[depth].AddFullNode(byteSize);
            c.TrackStorageNode(ctx.Level, byteSize, isLeaf: false, isBranch: true);
        }
        else
        {
            c.AccountFullNodes++;
            c.AccountNodeBytes += byteSize;
            c.AccountDepths[depth].AddFullNode(byteSize);
            c.TotalBranchNodes++;

            // Branch occupancy distribution — count non-null children.
            // ITreeVisitor contract guarantees a non-null node; no defensive null check.
            int childCount = 0;
            for (int i = 0; i < 16; i++)
            {
                if (!node.IsChildNull(i))
                    childCount++;
            }

            if (childCount > 0)
                c.BranchOccupancyHistogram[childCount - 1]++;
        }
    }

    public void VisitExtension(in StateCompositionContext ctx, TrieNode node)
    {
        VisitorCounters c = ctx.Counters ?? _localCounters.Value!;
        int byteSize = node.FullRlp.Length;
        int depth = Math.Min(ctx.Level, MaxDepthIndex);

        if (ctx.IsStorage)
        {
            c.StorageShortNodes++;
            c.StorageNodeBytes += byteSize;
            c.StorageDepths[depth].AddShortNode(byteSize);
            c.TrackStorageNode(ctx.Level, byteSize, isLeaf: false, isBranch: false);
        }
        else
        {
            c.AccountShortNodes++;
            c.AccountNodeBytes += byteSize;
            c.AccountDepths[depth].AddShortNode(byteSize);
        }
    }

    public void VisitLeaf(in StateCompositionContext ctx, TrieNode node)
    {
        VisitorCounters c = ctx.Counters ?? _localCounters.Value!;
        int byteSize = node.FullRlp.Length;
        int depth = Math.Min(ctx.Level, MaxDepthIndex);

        if (ctx.IsStorage)
        {
            c.StorageSlotsTotal++;
            c.StorageValueNodes++;
            c.StorageNodeBytes += byteSize;
            c.StorageDepths[depth].AddValueNode(byteSize);
            c.TrackStorageNode(ctx.Level, byteSize, isLeaf: true, isBranch: false);
        }
        else
        {
            c.AccountValueNodes++;
            c.AccountNodeBytes += byteSize;
            c.AccountDepths[depth].AddValueNode(byteSize);
        }
    }

    public void VisitAccount(in StateCompositionContext ctx, TrieNode node, in AccountStruct account)
    {
        VisitorCounters c = ctx.Counters ?? _localCounters.Value!;

        if (excludeStorage)
        {
            c.Flush();
        }
        else if (account.HasStorage)
        {
            c.ContractsWithStorage++;
            // Owner = account hash (keccak256 of address), reconstructed from the
            // accumulated nibble path. At leaf depth the path IS the account hash.
            c.BeginStorageTrie(account.StorageRoot, ctx.Path.Path);
        }
        else
            c.Flush();

        c.AccountsTotal++;

        if (account.HasCode)
        {
            c.ContractsTotal++;

            // Per-thread refcount: one increment per account observation. Summed across
            // threads in MergeFrom to give the total number of accounts pointing at each
            // distinct code hash. Seeds the state holder's incremental refcount tracker.
            ref int refcount = ref CollectionsMarshal.GetValueRefOrAddDefault(c.CodeHashRefcounts, account.CodeHash, out _);
            refcount++;

            // Dedup by codeHash across all worker threads: only the first observer
            // of a given codeHash pays the lookup cost and contributes bytes. Proxies,
            // minimal clones, and factory-deployed contracts share bytecode and
            // therefore contribute 0 bytes on subsequent observations.
            if (codeSizeLookup is not null && !_codeHashSizes.ContainsKey(account.CodeHash))
            {
                int size = codeSizeLookup(account.CodeHash);
                if (_codeHashSizes.TryAdd(account.CodeHash, size) && size > 0)
                    c.CodeBytes += size;
            }
        }

        if (account.IsTotallyEmpty)
            c.EmptyAccounts++;
    }

    // Ownership transfers on the three tracker maps: the visitor is one-shot
    // (internal sealed, used under a `using` in StateCompositionService) and the
    // holder takes direct ownership of the backing dictionaries in
    // InitializeIncremental. Callers must not reuse the visitor post-GetStats.
    // _codeHashSizes is a ConcurrentDictionary during the parallel scan — it is
    // converted to a plain Dictionary exactly once here so downstream consumers
    // do not pay the concurrent-dict overhead on every diff.
    public StateCompositionStats GetStats(long blockNumber, Hash256? stateRoot)
    {
        VisitorCounters agg = GetAggregated();

        return new StateCompositionStats
        {
            BlockNumber = blockNumber,
            StateRoot = stateRoot,
            AccountsTotal = agg.AccountsTotal,
            ContractsTotal = agg.ContractsTotal,
            ContractsWithStorage = agg.ContractsWithStorage,
            StorageSlotsTotal = agg.StorageSlotsTotal,
            AccountTrieNodeBytes = agg.AccountNodeBytes,
            StorageTrieNodeBytes = agg.StorageNodeBytes,
            AccountTrieFullNodes = agg.AccountFullNodes,
            AccountTrieShortNodes = agg.AccountShortNodes + agg.AccountValueNodes,
            AccountTrieValueNodes = agg.AccountValueNodes,
            StorageTrieFullNodes = agg.StorageFullNodes,
            StorageTrieShortNodes = agg.StorageShortNodes + agg.StorageValueNodes,
            StorageTrieValueNodes = agg.StorageValueNodes,
            EmptyAccounts = agg.EmptyAccounts,
            CodeBytesTotal = agg.CodeBytes,
            SlotCountHistogram = ImmutableArray.Create<long>(agg.SlotCountHistogram[..]),
            TopContractsByDepth = BuildSortedTopN(agg.TopN.TopByDepth, agg.TopN.TopByDepthCount, TopNTracker.CompareByDepth),
            TopContractsByNodes = BuildSortedTopN(agg.TopN.TopByNodes, agg.TopN.TopByNodesCount, TopNTracker.CompareByTotalNodes),
            TopContractsByValueNodes = BuildSortedTopN(agg.TopN.TopByValueNodes, agg.TopN.TopByValueNodesCount, TopNTracker.CompareByValueNodes),
            TopContractsBySize = BuildSortedTopN(agg.TopN.TopBySize, agg.TopN.TopBySizeCount, TopNTracker.CompareBySize),
            SlotCountByAddress = agg.SlotCountsByOwner,
            CodeHashSizes = new Dictionary<ValueHash256, int>(_codeHashSizes),
            CodeHashRefcounts = agg.CodeHashRefcounts,
        };
    }

    /// <summary>
    /// Progress-logger snapshot. Runs on the 8-second timer thread while scan
    /// workers write the same counters without locks — Volatile.Read guarantees
    /// torn-free 64-bit loads on every .NET runtime, not just x64. Values are
    /// inherently best-effort (no happens-before across counters) and fine for
    /// log-line rendering; correctness-critical totals flow through GetStats()
    /// after the scan has joined all workers.
    /// </summary>
    public ScanSnapshot GetSnapshot()
    {
        long accounts = 0, contracts = 0, withStorage = 0, slots = 0, nodes = 0, bytes = 0;
        foreach (VisitorCounters c in _localCounters.Values)
        {
            accounts += Volatile.Read(ref c.AccountsTotal);
            contracts += Volatile.Read(ref c.ContractsTotal);
            withStorage += Volatile.Read(ref c.ContractsWithStorage);
            slots += Volatile.Read(ref c.StorageSlotsTotal);
            nodes += Volatile.Read(ref c.AccountFullNodes)
                   + Volatile.Read(ref c.AccountShortNodes)
                   + Volatile.Read(ref c.AccountValueNodes)
                   + Volatile.Read(ref c.StorageFullNodes)
                   + Volatile.Read(ref c.StorageShortNodes)
                   + Volatile.Read(ref c.StorageValueNodes);
            bytes += Volatile.Read(ref c.AccountNodeBytes) + Volatile.Read(ref c.StorageNodeBytes);
        }

        return new ScanSnapshot(accounts, contracts, withStorage, slots, nodes, bytes);
    }

    public TrieDepthDistribution GetTrieDistribution()
    {
        VisitorCounters agg = GetAggregated();

        return new TrieDepthDistribution
        {
            AccountTrieLevels = LevelStatsBuilder.BuildCompact(agg.AccountDepths[..]),
            StorageTrieLevels = LevelStatsBuilder.BuildCompact(agg.StorageDepths[..]),
            AvgAccountPathDepth = CalcAvgDepth(agg.AccountDepths[..]),
            AvgStoragePathDepth = CalcAvgDepth(agg.StorageDepths[..]),
            MaxAccountDepth = CalcMaxDepth(agg.AccountDepths[..]),
            MaxStorageDepth = CalcMaxDepth(agg.StorageDepths[..]),
            AvgBranchOccupancy = agg.TotalBranchNodes > 0
                ? (double)agg.TotalBranchChildren / agg.TotalBranchNodes
                : 0.0,
            StorageMaxDepthHistogram = ImmutableArray.Create<long>(agg.StorageMaxDepthHistogram[..]),
            BranchOccupancyDistribution = ImmutableArray.Create<long>(agg.BranchOccupancyHistogram[..]),
        };
    }

    private VisitorCounters GetAggregated()
    {
        if (_aggregated is not null)
            return _aggregated;

        VisitorCounters agg = new(topN);
        foreach (VisitorCounters local in _localCounters.Values)
        {
            local.Flush(); // Finalize last contract's storage trie per thread
            agg.MergeFrom(local);
        }

        _aggregated = agg;
        return agg;
    }

    private static ImmutableArray<TopContractEntry> BuildSortedTopN(
        TopContractEntry[] entries, int count, TopNTracker.EntryComparer comparer)
    {
        if (count == 0)
            return [];

        TopContractEntry[] sorted = entries.AsSpan(0, count).ToArray();
        Array.Sort(sorted, 0, count, Comparer<TopContractEntry>.Create((a, b) => comparer(b, a)));
        return System.Runtime.InteropServices.ImmutableCollectionsMarshal.AsImmutableArray(sorted);
    }

    private static double CalcAvgDepth(ReadOnlySpan<DepthCounter> depths)
    {
        long totalNodes = 0;
        long weightedSum = 0;
        for (int i = 0; i < depths.Length; i++)
        {
            long nodesAtDepth = depths[i].FullNodes + depths[i].ShortNodes + depths[i].ValueNodes;
            totalNodes += nodesAtDepth;
            weightedSum += nodesAtDepth * i;
        }

        return totalNodes > 0 ? (double)weightedSum / totalNodes : 0.0;
    }

    private static int CalcMaxDepth(ReadOnlySpan<DepthCounter> depths)
    {
        for (int i = depths.Length - 1; i >= 0; i--)
        {
            if (depths[i].FullNodes + depths[i].ShortNodes + depths[i].ValueNodes > 0)
                return i;
        }

        return 0;
    }

    public void Dispose() => _localCounters.Dispose();
}

internal readonly record struct ScanSnapshot(
    long Accounts, long Contracts, long ContractsWithStorage,
    long StorageSlots, long Nodes, long Bytes)
{
    internal static string Fmt(double value) =>
        value >= 1_000_000 ? $"{value / 1_000_000.0:F1}M" : $"{value:N0}";
}
