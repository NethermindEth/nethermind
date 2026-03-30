// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Immutable;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Trie;

namespace Nethermind.StateComposition;

/// <summary>
/// Enhanced ITreeVisitor that collects all composition metrics in a single pass.
/// Uses ThreadLocal&lt;VisitorCounters&gt; for lock-free scaling to 64+ cores.
/// </summary>
public sealed class StateCompositionVisitor(ILogManager logManager)
    : ITreeVisitor<OldStyleTrieVisitContext>, IDisposable
{
    private readonly ILogger _logger = logManager.GetClassLogger();

    private readonly ThreadLocal<VisitorCounters> _localCounters =
        new(() => new VisitorCounters(), trackAllValues: true);

    private const int MaxDepthIndex = VisitorCounters.MaxTrackedDepth - 1;

    public bool IsFullDbScan => true;
    public ReadFlags ExtraReadFlag => ReadFlags.HintCacheMiss;
    public bool ExpectAccounts => true;

    public bool ShouldVisit(in OldStyleTrieVisitContext ctx, in ValueHash256 nextNode)
    {
        // Track branch occupancy: ShouldVisit is called for each non-null child
        // of a branch node, with BranchChildIndex set to the child's position.
        // Only count account trie children to match TotalBranchNodes (account-only).
        if (ctx is { BranchChildIndex: not null, IsStorage: false })
            _localCounters.Value!.TotalBranchChildren++;

        return true;
    }

    public void VisitTree(in OldStyleTrieVisitContext ctx, in ValueHash256 rootHash)
    {
    }

    public void VisitMissingNode(in OldStyleTrieVisitContext ctx, in ValueHash256 nodeHash)
    {
        if (_logger.IsWarn)
            _logger.Warn($"StateComposition: missing node at depth {ctx.Level}, storage={ctx.IsStorage}");
    }

    public void VisitBranch(in OldStyleTrieVisitContext ctx, TrieNode node)
    {
        VisitorCounters c = _localCounters.Value!;
        int byteSize = node.FullRlp.Length;
        int depth = Math.Min(ctx.Level, MaxDepthIndex);

        if (ctx.IsStorage)
        {
            c.StorageBranches++;
            c.StorageNodeBytes += byteSize;
            c.StorageDepths[depth].AddBranch(byteSize);
        }
        else
        {
            c.AccountBranches++;
            c.AccountNodeBytes += byteSize;
            c.AccountDepths[depth].AddBranch(byteSize);
            c.TotalBranchNodes++;
        }
    }

    public void VisitExtension(in OldStyleTrieVisitContext ctx, TrieNode node)
    {
        VisitorCounters c = _localCounters.Value!;
        int byteSize = node.FullRlp.Length;
        int depth = Math.Min(ctx.Level, MaxDepthIndex);

        if (ctx.IsStorage)
        {
            c.StorageExtensions++;
            c.StorageNodeBytes += byteSize;
            c.StorageDepths[depth].AddExtension(byteSize);
        }
        else
        {
            c.AccountExtensions++;
            c.AccountNodeBytes += byteSize;
            c.AccountDepths[depth].AddExtension(byteSize);
        }
    }

    public void VisitLeaf(in OldStyleTrieVisitContext ctx, TrieNode node)
    {
        VisitorCounters c = _localCounters.Value!;
        int byteSize = node.FullRlp.Length;
        int depth = Math.Min(ctx.Level, MaxDepthIndex);

        if (ctx.IsStorage)
        {
            c.StorageSlotsTotal++;
            c.StorageLeaves++;
            c.StorageNodeBytes += byteSize;
            c.StorageDepths[depth].AddLeaf(byteSize);
        }
        else
        {
            c.AccountLeaves++;
            c.AccountNodeBytes += byteSize;
            c.AccountDepths[depth].AddLeaf(byteSize);
        }
    }

    public void VisitAccount(in OldStyleTrieVisitContext ctx, TrieNode node, in AccountStruct account)
    {
        VisitorCounters c = _localCounters.Value!;
        c.AccountsTotal++;

        if (account.HasCode)
            c.ContractsTotal++;

        if (account.HasStorage)
            c.ContractsWithStorage++;
    }

    public StateCompositionStats GetStats(long blockNumber, Hash256? stateRoot)
    {
        VisitorCounters agg = AggregateCounters();

        return new StateCompositionStats
        {
            BlockNumber = blockNumber,
            StateRoot = stateRoot,
            AccountsTotal = agg.AccountsTotal,
            ContractsTotal = agg.ContractsTotal,
            ContractsWithStorage = agg.ContractsWithStorage,
            StorageSlotsTotal = agg.StorageSlotsTotal,
            TotalCodeSize = agg.TotalCodeSize,
            AccountBytes = agg.AccountNodeBytes,
            StorageBytes = agg.StorageNodeBytes,
            AccountTrieNodeBytes = agg.AccountNodeBytes,
            StorageTrieNodeBytes = agg.StorageNodeBytes,
            AccountTrieBranchNodes = agg.AccountBranches,
            AccountTrieExtensionNodes = agg.AccountExtensions,
            AccountTrieLeafNodes = agg.AccountLeaves,
            StorageTrieBranchNodes = agg.StorageBranches,
            StorageTrieExtensionNodes = agg.StorageExtensions,
            StorageTrieLeafNodes = agg.StorageLeaves,
        };
    }

    public TrieDepthDistribution GetTrieDistribution()
    {
        VisitorCounters agg = AggregateCounters();

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
        };
    }

    private VisitorCounters AggregateCounters()
    {
        VisitorCounters agg = new();
        foreach (VisitorCounters local in _localCounters.Values)
            agg.MergeFrom(local);

        return agg;
    }

    private static ImmutableArray<TrieLevelStat> BuildLevelStats(DepthCounter[] depths)
    {
        ImmutableArray<TrieLevelStat>.Builder builder = ImmutableArray.CreateBuilder<TrieLevelStat>();
        for (int i = 0; i < depths.Length; i++)
        {
            if (depths[i].Branches + depths[i].Extensions + depths[i].Leaves == 0)
                continue;

            builder.Add(new TrieLevelStat
            {
                Depth = i,
                BranchNodes = depths[i].Branches,
                ExtensionNodes = depths[i].Extensions,
                LeafNodes = depths[i].Leaves,
                ByteSize = depths[i].ByteSize,
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
            long nodesAtDepth = depths[i].Branches + depths[i].Extensions + depths[i].Leaves;
            totalNodes += nodesAtDepth;
            weightedSum += nodesAtDepth * i;
        }

        return totalNodes > 0 ? (double)weightedSum / totalNodes : 0.0;
    }

    private static int CalcMaxDepth(DepthCounter[] depths)
    {
        for (int i = depths.Length - 1; i >= 0; i--)
        {
            if (depths[i].Branches + depths[i].Extensions + depths[i].Leaves > 0)
                return i;
        }

        return 0;
    }

    public void Dispose()
    {
        _localCounters.Dispose();
    }
}
