// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Threading;
using Nethermind.Core.Attributes;

using Nethermind.StateComposition.Data;
using Nethermind.StateComposition.Diff;

namespace Nethermind.StateComposition;

public static class Metrics
{
    [GaugeMetric]
    [Description("Total accounts in state")]
    public static long StateCompAccountsTotal { get; set; }

    [GaugeMetric]
    [Description("Total contracts (accounts with code)")]
    public static long StateCompContractsTotal { get; set; }

    [GaugeMetric]
    [Description("Total storage slots across all contracts")]
    public static long StateCompStorageSlotsTotal { get; set; }

    [GaugeMetric]
    [Description("Branch nodes in account trie")]
    public static long StateCompAccountTrieBranches { get; set; }

    [GaugeMetric]
    [Description("Extension nodes in account trie")]
    public static long StateCompAccountTrieExtensions { get; set; }

    [GaugeMetric]
    [Description("Leaf nodes in account trie")]
    public static long StateCompAccountTrieLeaves { get; set; }

    [GaugeMetric]
    [Description("Total bytes of account trie nodes")]
    public static long StateCompAccountTrieBytes { get; set; }

    [GaugeMetric]
    [Description("Branch nodes in all storage tries")]
    public static long StateCompStorageTrieBranches { get; set; }

    [GaugeMetric]
    [Description("Extension nodes in all storage tries")]
    public static long StateCompStorageTrieExtensions { get; set; }

    [GaugeMetric]
    [Description("Leaf nodes in all storage tries")]
    public static long StateCompStorageTrieLeaves { get; set; }

    [GaugeMetric]
    [Description("Total bytes of all storage trie nodes")]
    public static long StateCompStorageTrieBytes { get; set; }

    [GaugeMetric]
    [Description("Block number of latest incremental update")]
    public static long StateCompIncrementalBlock { get; set; }

    [GaugeMetric]
    [Description("Diffs applied since last full scan")]
    public static long StateCompDiffsSinceBaseline { get; set; }

    [GaugeMetric]
    [Description("Duration of last completed full scan in seconds")]
    public static double StateCompScanDurationSeconds { get; set; }

    [GaugeMetric]
    [Description("Block number of last completed full scan")]
    public static long StateCompScanBlock { get; set; }

    [CounterMetric]
    [Description("Total full scans completed")]
    public static long StateCompScansCompleted { get; set; }

    [CounterMetric]
    [Description("Total incremental diffs applied")]
    public static long StateCompDiffsApplied { get; set; }

    [CounterMetric]
    [Description("Total diff computation errors")]
    public static long StateCompDiffErrors { get; set; }

    [CounterMetric]
    [Description("Times the incremental baseline was detected as stale (prevRoot missing from DB) and auto-rescan was scheduled")]
    public static long StateCompBaselineInvalidations { get; set; }

    private static long _stateCompScanMissingNodes;

    [CounterMetric]
    [Description("Times a full or contract scan encountered a missing trie node (pruned/corrupt DB); the resulting stats are incomplete")]
    public static long StateCompScanMissingNodes => _stateCompScanMissingNodes;

    internal static void IncrementScanMissingNodes() => Interlocked.Increment(ref _stateCompScanMissingNodes);

    [GaugeMetric]
    [Description("Contracts with non-empty storage")]
    public static long StateCompContractsWithStorage { get; set; }

    [GaugeMetric]
    [Description("Accounts with no code and no storage")]
    public static long StateCompEmptyAccounts { get; set; }

    [GaugeMetric]
    [Description("Aggregate contract bytecode size, deduplicated by code hash")]
    public static long StateCompCodeBytesTotal { get; set; }

    [GaugeMetric]
    [Description("Weighted average depth of account trie leaf paths")]
    public static double StateCompAvgAccountPathDepth { get; set; }

    [GaugeMetric]
    [Description("Weighted average depth of storage trie leaf paths")]
    public static double StateCompAvgStoragePathDepth { get; set; }

    [GaugeMetric]
    [Description("Deepest occupied level of the account trie")]
    public static long StateCompMaxAccountDepth { get; set; }

    [GaugeMetric]
    [Description("Deepest occupied level across all storage tries (Geth convention: raw depth + 1)")]
    public static long StateCompMaxStorageDepth { get; set; }

    [GaugeMetric]
    [Description("Average children per account-trie branch node")]
    public static double StateCompAvgBranchOccupancy { get; set; }

    // Per-depth node counts. String labels (not enums) so Prometheus gets the
    // lowercase form expected by PromQL: KeyIsLabelGaugeMetricUpdater emits
    // each ValueTuple component via ToString(), and enum.ToString() is PascalCase.
    [GaugeMetric]
    [KeyIsLabel("trie", "depth", "kind")]
    [Description("Trie nodes per depth, by trie and node kind")]
    public static ConcurrentDictionary<(string trie, int depth, string kind), long> StateCompTrieDepthNodes { get; } = new();

    [GaugeMetric]
    [KeyIsLabel("trie", "depth")]
    [Description("Trie bytes per depth")]
    public static ConcurrentDictionary<(string trie, int depth), long> StateCompTrieDepthBytes { get; } = new();

    [GaugeMetric]
    [KeyIsLabel("children")]
    [Description("Account trie branch nodes grouped by child count")]
    public static ConcurrentDictionary<int, long> StateCompAccountBranchOccupancy { get; } = new();

    [GaugeMetric]
    [KeyIsLabel("bucket")]
    [Description("Contracts grouped by log2 storage slot count bucket")]
    public static ConcurrentDictionary<int, long> StateCompSlotCountHistogram { get; } = new();

    internal static void UpdateFromCumulativeStats(CumulativeTrieStats s)
    {
        StateCompAccountsTotal = s.AccountsTotal;
        StateCompContractsTotal = s.ContractsTotal;
        StateCompStorageSlotsTotal = s.StorageSlotsTotal;
        StateCompAccountTrieBranches = s.AccountTrieBranches;
        StateCompAccountTrieExtensions = s.AccountTrieExtensions;
        StateCompAccountTrieLeaves = s.AccountTrieLeaves;
        StateCompAccountTrieBytes = s.AccountTrieBytes;
        StateCompStorageTrieBranches = s.StorageTrieBranches;
        StateCompStorageTrieExtensions = s.StorageTrieExtensions;
        StateCompStorageTrieLeaves = s.StorageTrieLeaves;
        StateCompStorageTrieBytes = s.StorageTrieBytes;
        StateCompContractsWithStorage = s.ContractsWithStorage;
        StateCompEmptyAccounts = s.EmptyAccounts;
        StateCompCodeBytesTotal = s.CodeBytesTotal;

        if (!s.SlotCountHistogram.IsDefault)
        {
            for (int i = 0; i < s.SlotCountHistogram.Length; i++)
                StateCompSlotCountHistogram[i] = s.SlotCountHistogram[i];
        }
    }

    internal static void UpdateDepthDistribution(CumulativeDepthStats s)
    {
        if (!s.IsSeeded) return;

        for (int d = 0; d < 16; d++)
        {
            StateCompTrieDepthNodes[("account", d, "full")] = s.AccountFullNodes[d];
            StateCompTrieDepthNodes[("account", d, "short")] = s.AccountShortNodes[d];
            StateCompTrieDepthNodes[("account", d, "value")] = d > 0 ? s.AccountValueNodes[d - 1] : 0;
            StateCompTrieDepthBytes[("account", d)] = s.AccountNodeBytes[d];

            StateCompTrieDepthNodes[("storage", d, "full")] = s.StorageFullNodes[d];
            StateCompTrieDepthNodes[("storage", d, "short")] = s.StorageShortNodes[d];
            StateCompTrieDepthNodes[("storage", d, "value")] = d > 0 ? s.StorageValueNodes[d - 1] : 0;
            StateCompTrieDepthBytes[("storage", d)] = s.StorageNodeBytes[d];

            StateCompAccountBranchOccupancy[d + 1] = s.BranchOccupancy[d];
        }

        StateCompAvgAccountPathDepth = WeightedAvgDepth(s.AccountFullNodes, s.AccountShortNodes, s.AccountValueNodes);
        StateCompAvgStoragePathDepth = WeightedAvgDepth(s.StorageFullNodes, s.StorageShortNodes, s.StorageValueNodes);
        StateCompMaxAccountDepth = LastNonZeroDepth(s.AccountFullNodes, s.AccountShortNodes, s.AccountValueNodes);
        StateCompMaxStorageDepth = LastNonZeroDepth(s.StorageFullNodes, s.StorageShortNodes, s.StorageValueNodes) + 1;
        StateCompAvgBranchOccupancy = s.TotalBranchNodes > 0 ? (double)s.TotalBranchChildren / s.TotalBranchNodes : 0.0;
    }

    private static double WeightedAvgDepth(ReadOnlySpan<long> full, ReadOnlySpan<long> shrt, ReadOnlySpan<long> value)
    {
        long totalNodes = 0;
        long weightedSum = 0;
        for (int i = 0; i < 16; i++)
        {
            long nodesAtDepth = full[i] + shrt[i] + value[i];
            totalNodes += nodesAtDepth;
            weightedSum += nodesAtDepth * i;
        }
        return totalNodes > 0 ? (double)weightedSum / totalNodes : 0.0;
    }

    private static long LastNonZeroDepth(ReadOnlySpan<long> full, ReadOnlySpan<long> shrt, ReadOnlySpan<long> value)
    {
        for (int i = 15; i >= 0; i--)
        {
            if (full[i] + shrt[i] + value[i] > 0) return i;
        }
        return 0;
    }
}
