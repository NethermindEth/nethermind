// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Trie;

namespace Nethermind.StateComposition;

/// <summary>
/// Enhanced ITreeVisitor that collects all composition metrics in a single pass.
/// Uses ThreadLocal&lt;VisitorCounters&gt; for lock-free scaling to 64+ cores.
/// Uses StateCompositionContext for path tracking to reconstruct account hashes (Owner).
/// </summary>
public sealed class StateCompositionVisitor : ITreeVisitor<StateCompositionContext>, IDisposable
{
    private readonly ILogger _logger;
    private readonly CancellationToken _ct;
    private readonly int _topN;
    private readonly bool _excludeStorage;

    private readonly ThreadLocal<VisitorCounters> _localCounters;

    private const int MaxDepthIndex = VisitorCounters.MaxTrackedDepth - 1;

    // Cached aggregation result — computed once after scan completes
    private VisitorCounters? _aggregated;

    public bool IsFullDbScan => true;
    public ReadFlags ExtraReadFlag => ReadFlags.HintCacheMiss;
    public bool ExpectAccounts => true;

    public StateCompositionVisitor(ILogManager logManager, CancellationToken ct = default,
        int topN = 20, bool excludeStorage = false)
    {
        _logger = logManager.GetClassLogger();
        _ct = ct;
        _topN = topN;
        _excludeStorage = excludeStorage;
        _localCounters = new(() => new VisitorCounters(topN), trackAllValues: true);
    }

    public bool ShouldVisit(in StateCompositionContext ctx, in ValueHash256 nextNode)
    {
        if (_ct.IsCancellationRequested)
            return false;

        // Track branch occupancy: ShouldVisit is called for each non-null child
        // of a branch node, with BranchChildIndex set to the child's position.
        // Only count account trie children to match TotalBranchNodes (account-only).
        if (ctx is { BranchChildIndex: not null, IsStorage: false })
            _localCounters.Value!.TotalBranchChildren++;

        return true;
    }

    public void VisitTree(in StateCompositionContext ctx, in ValueHash256 rootHash)
    {
    }

    public void VisitMissingNode(in StateCompositionContext ctx, in ValueHash256 nodeHash)
    {
        if (_logger.IsWarn)
            _logger.Warn($"StateComposition: missing node at depth {ctx.Level}, storage={ctx.IsStorage}");
    }

    public void VisitBranch(in StateCompositionContext ctx, TrieNode node)
    {
        VisitorCounters c = _localCounters.Value!;
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
        }
    }

    public void VisitExtension(in StateCompositionContext ctx, TrieNode node)
    {
        VisitorCounters c = _localCounters.Value!;
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
        VisitorCounters c = _localCounters.Value!;
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
        VisitorCounters c = _localCounters.Value!;

        if (_excludeStorage)
        {
            // ExcludeStorage mode: flush any pending and skip storage traversal
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
        {
            c.Flush();
        }

        c.AccountsTotal++;

        if (account.HasCode)
            c.ContractsTotal++;
    }

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
            TopContractsByDepth = BuildSortedTopN(agg.TopByDepth, agg.TopByDepthCount, VisitorCounters.CompareByDepth),
            TopContractsByNodes = BuildSortedTopN(agg.TopByNodes, agg.TopByNodesCount, VisitorCounters.CompareByTotalNodes),
            TopContractsByValueNodes = BuildSortedTopN(agg.TopByValueNodes, agg.TopByValueNodesCount, VisitorCounters.CompareByValueNodes),
        };
    }

    public TrieDepthDistribution GetTrieDistribution()
    {
        VisitorCounters agg = GetAggregated();

        return new TrieDepthDistribution
        {
            AccountTrieLevels = BuildLevelStats(agg.AccountDepths),
            StorageTrieLevels = BuildLevelStats(agg.StorageDepths),
            AvgAccountPathDepth = CalcAvgDepth(agg.AccountDepths),
            AvgStoragePathDepth = CalcAvgDepth(agg.StorageDepths),
            MaxAccountDepth = CalcMaxDepth(agg.AccountDepths),
            MaxStorageDepth = CalcMaxDepth(agg.StorageDepths),
            AvgBranchOccupancy = agg.TotalBranchNodes > 0
                ? (double)agg.TotalBranchChildren / agg.TotalBranchNodes
                : 0.0,
            StorageMaxDepthHistogram = ImmutableArray.Create(agg.StorageMaxDepthHistogram),
        };
    }

    /// <summary>
    /// Returns cached aggregation or computes it once. Flushes all per-thread
    /// storage trie accumulators before merging.
    /// </summary>
    private VisitorCounters GetAggregated()
    {
        if (_aggregated is not null)
            return _aggregated;

        VisitorCounters agg = new(_topN);
        foreach (VisitorCounters local in _localCounters.Values)
        {
            local.Flush(); // Finalize last contract's storage trie per thread
            agg.MergeFrom(local);
        }

        _aggregated = agg;
        return agg;
    }

    private delegate int EntryComparer(in TopContractEntry a, in TopContractEntry b);

    private static ImmutableArray<TopContractEntry> BuildSortedTopN(
        TopContractEntry[] entries, int count, EntryComparer comparer)
    {
        if (count == 0)
            return ImmutableArray<TopContractEntry>.Empty;

        // Sort descending using the deterministic multi-field comparator
        TopContractEntry[] sorted = entries.Take(count).ToArray();
        Array.Sort(sorted, (a, b) => comparer(b, a)); // Reverse for descending
        return ImmutableArray.Create(sorted);
    }

    private static ImmutableArray<TrieLevelStat> BuildLevelStats(DepthCounter[] depths)
    {
        ImmutableArray<TrieLevelStat>.Builder builder = ImmutableArray.CreateBuilder<TrieLevelStat>();
        for (int i = 0; i < depths.Length; i++)
        {
            // Geth counts valueNode at depth+1 from its leaf shortNode, so
            // Value[i] in Geth output = leaves physically at depth i-1 in our counter.
            long shiftedValue = i > 0 ? depths[i - 1].ValueNodes : 0;

            if (depths[i].FullNodes + depths[i].ShortNodes + depths[i].ValueNodes == 0
                && shiftedValue == 0)
                continue;

            builder.Add(new TrieLevelStat
            {
                Depth = i,
                FullNodeCount = depths[i].FullNodes,
                ShortNodeCount = depths[i].ShortNodes + depths[i].ValueNodes,
                ValueNodeCount = shiftedValue,
                TotalSize = depths[i].TotalSize,
            });
        }

        return builder.ToImmutable();
    }

    private static double CalcAvgDepth(DepthCounter[] depths)
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

    private static int CalcMaxDepth(DepthCounter[] depths)
    {
        for (int i = depths.Length - 1; i >= 0; i--)
        {
            if (depths[i].FullNodes + depths[i].ShortNodes + depths[i].ValueNodes > 0)
                return i;
        }

        return 0;
    }

    public void Dispose()
    {
        _localCounters.Dispose();
    }
}
