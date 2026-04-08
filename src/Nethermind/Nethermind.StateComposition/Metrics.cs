// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.ComponentModel;
using Nethermind.Core.Attributes;

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

    [GaugeMetric]
    [Description("Contracts with non-empty storage")]
    public static long StateCompContractsWithStorage { get; set; }

    [GaugeMetric]
    [Description("Accounts with no code and no storage")]
    public static long StateCompEmptyAccounts { get; set; }

    public static void UpdateFromCumulativeStats(CumulativeSizeStats s)
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
    }
}
