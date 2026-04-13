// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Immutable;
using System.ComponentModel;
using Nethermind.Core.Attributes;

using Nethermind.StateComposition.Data;

namespace Nethermind.StateComposition;

public static partial class Metrics
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

    [GaugeMetric]
    [Description("Aggregate contract bytecode size, deduplicated by code hash")]
    public static long StateCompCodeBytesTotal { get; set; }

    // Log-bucketed slot-count histogram gauges. Bucket i counts contracts whose
    // slot count satisfies min(15, floor(log2(slotCount + 1))) == i. Emitted as
    // 16 flat gauges rather than a Prometheus histogram so a single alert rule
    // can compare buckets without rebinning on the dashboard side.
    [GaugeMetric]
    [Description("Contracts with slot-count bucket 0 (floor(log2(slots+1)) == 0)")]
    public static long StateCompSlotCountBucket0 { get; set; }

    [GaugeMetric]
    [Description("Contracts with slot-count bucket 1 (floor(log2(slots+1)) == 1)")]
    public static long StateCompSlotCountBucket1 { get; set; }

    [GaugeMetric]
    [Description("Contracts with slot-count bucket 2 (floor(log2(slots+1)) == 2)")]
    public static long StateCompSlotCountBucket2 { get; set; }

    [GaugeMetric]
    [Description("Contracts with slot-count bucket 3 (floor(log2(slots+1)) == 3)")]
    public static long StateCompSlotCountBucket3 { get; set; }

    [GaugeMetric]
    [Description("Contracts with slot-count bucket 4 (floor(log2(slots+1)) == 4)")]
    public static long StateCompSlotCountBucket4 { get; set; }

    [GaugeMetric]
    [Description("Contracts with slot-count bucket 5 (floor(log2(slots+1)) == 5)")]
    public static long StateCompSlotCountBucket5 { get; set; }

    [GaugeMetric]
    [Description("Contracts with slot-count bucket 6 (floor(log2(slots+1)) == 6)")]
    public static long StateCompSlotCountBucket6 { get; set; }

    [GaugeMetric]
    [Description("Contracts with slot-count bucket 7 (floor(log2(slots+1)) == 7)")]
    public static long StateCompSlotCountBucket7 { get; set; }

    [GaugeMetric]
    [Description("Contracts with slot-count bucket 8 (floor(log2(slots+1)) == 8)")]
    public static long StateCompSlotCountBucket8 { get; set; }

    [GaugeMetric]
    [Description("Contracts with slot-count bucket 9 (floor(log2(slots+1)) == 9)")]
    public static long StateCompSlotCountBucket9 { get; set; }

    [GaugeMetric]
    [Description("Contracts with slot-count bucket 10 (floor(log2(slots+1)) == 10)")]
    public static long StateCompSlotCountBucket10 { get; set; }

    [GaugeMetric]
    [Description("Contracts with slot-count bucket 11 (floor(log2(slots+1)) == 11)")]
    public static long StateCompSlotCountBucket11 { get; set; }

    [GaugeMetric]
    [Description("Contracts with slot-count bucket 12 (floor(log2(slots+1)) == 12)")]
    public static long StateCompSlotCountBucket12 { get; set; }

    [GaugeMetric]
    [Description("Contracts with slot-count bucket 13 (floor(log2(slots+1)) == 13)")]
    public static long StateCompSlotCountBucket13 { get; set; }

    [GaugeMetric]
    [Description("Contracts with slot-count bucket 14 (floor(log2(slots+1)) == 14)")]
    public static long StateCompSlotCountBucket14 { get; set; }

    [GaugeMetric]
    [Description("Contracts with slot-count bucket 15 (floor(log2(slots+1)) >= 15; saturating)")]
    public static long StateCompSlotCountBucket15 { get; set; }

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
        StateCompContractsWithStorage = s.ContractsWithStorage;
        StateCompEmptyAccounts = s.EmptyAccounts;
        StateCompCodeBytesTotal = s.CodeBytesTotal;
        UpdateSlotCountHistogram(s.SlotCountHistogram);
    }

    /// <summary>
    /// Fan out the 16-bucket slot-count histogram into per-bucket gauges.
    /// Caller must supply a length-16 array — producers (visitor + snapshot
    /// decoder) always do, so this method does not defend against shorter
    /// inputs.
    /// </summary>
    private static void UpdateSlotCountHistogram(ImmutableArray<long> h)
    {
        StateCompSlotCountBucket0 = h[0];
        StateCompSlotCountBucket1 = h[1];
        StateCompSlotCountBucket2 = h[2];
        StateCompSlotCountBucket3 = h[3];
        StateCompSlotCountBucket4 = h[4];
        StateCompSlotCountBucket5 = h[5];
        StateCompSlotCountBucket6 = h[6];
        StateCompSlotCountBucket7 = h[7];
        StateCompSlotCountBucket8 = h[8];
        StateCompSlotCountBucket9 = h[9];
        StateCompSlotCountBucket10 = h[10];
        StateCompSlotCountBucket11 = h[11];
        StateCompSlotCountBucket12 = h[12];
        StateCompSlotCountBucket13 = h[13];
        StateCompSlotCountBucket14 = h[14];
        StateCompSlotCountBucket15 = h[15];
    }
}
