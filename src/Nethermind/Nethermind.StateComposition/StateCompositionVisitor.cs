// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
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

    // Balance bucket boundaries (Wei). 1 ETH = 10^18 Wei.
    private static readonly UInt256 Wei001Eth = UInt256.Parse("10000000000000000");       // 10^16
    private static readonly UInt256 Wei1Eth   = UInt256.Parse("1000000000000000000");     // 10^18
    private static readonly UInt256 Wei10Eth  = UInt256.Parse("10000000000000000000");    // 10^19
    private static readonly UInt256 Wei100Eth = UInt256.Parse("100000000000000000000");   // 10^20
    private static readonly UInt256 Wei1KEth  = UInt256.Parse("1000000000000000000000");  // 10^21
    private static readonly UInt256 Wei10KEth = UInt256.Parse("10000000000000000000000"); // 10^22

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

            // Branch occupancy distribution — count non-null children.
            // Guard with try-catch: IsChildNull requires fully decoded RLP which
            // may not be available in unit tests with stub TrieNodes.
            try
            {
                int childCount = 0;
                for (int i = 0; i < 16; i++)
                {
                    if (!node.IsChildNull(i))
                        childCount++;
                }

                if (childCount > 0)
                    c.BranchOccupancyHistogram[childCount - 1]++;
            }
            catch (Exception)
            {
                // Node RLP not fully decoded — skip occupancy tracking for this node
            }
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

        if (account.IsTotallyEmpty)
            c.EmptyAccounts++;

        c.BalanceBuckets[BalanceBucket(account.Balance)]++;
        c.NonceBuckets[NonceBucket(account.Nonce)]++;
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
            EmptyAccounts = agg.EmptyAccounts,
            TopContractsByDepth = BuildSortedTopN(agg.TopN.TopByDepth, agg.TopN.TopByDepthCount, TopNTracker.CompareByDepth),
            TopContractsByNodes = BuildSortedTopN(agg.TopN.TopByNodes, agg.TopN.TopByNodesCount, TopNTracker.CompareByTotalNodes),
            TopContractsByValueNodes = BuildSortedTopN(agg.TopN.TopByValueNodes, agg.TopN.TopByValueNodesCount, TopNTracker.CompareByValueNodes),
            TopContractsBySize = BuildSortedTopN(agg.TopN.TopBySize, agg.TopN.TopBySizeCount, TopNTracker.CompareBySize),
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
            BranchOccupancyDistribution = ImmutableArray.Create(agg.BranchOccupancyHistogram),
            BalanceDistribution = ImmutableArray.Create(agg.BalanceBuckets),
            NonceDistribution = ImmutableArray.Create(agg.NonceBuckets),
            StorageSlotDistribution = ImmutableArray.Create(agg.StorageSlotBuckets),
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

    private static ImmutableArray<TopContractEntry> BuildSortedTopN(
        TopContractEntry[] entries, int count, TopNTracker.EntryComparer comparer)
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

    /// <summary>
    /// Maps an account balance (Wei) to a distribution bucket index (0-7).
    /// Buckets: 0 | &lt;0.01 ETH | 0.01-1 | 1-10 | 10-100 | 100-1K | 1K-10K | 10K+
    /// </summary>
    private static int BalanceBucket(in UInt256 balance)
    {
        if (balance.IsZero) return 0;
        if (balance < Wei001Eth) return 1;
        if (balance < Wei1Eth) return 2;
        if (balance < Wei10Eth) return 3;
        if (balance < Wei100Eth) return 4;
        if (balance < Wei1KEth) return 5;
        if (balance < Wei10KEth) return 6;
        return 7;
    }

    /// <summary>
    /// Maps a nonce to a distribution bucket index (0-5).
    /// Buckets: 0 | 1 | 2-10 | 11-100 | 101-1K | 1K+
    /// </summary>
    private static int NonceBucket(in UInt256 nonce)
    {
        if (nonce.IsZero) return 0;
        if (nonce == UInt256.One) return 1;
        if (nonce <= 10) return 2;
        if (nonce <= 100) return 3;
        if (nonce <= 1000) return 4;
        return 5;
    }

    public void Dispose()
    {
        _localCounters.Dispose();
    }
}
