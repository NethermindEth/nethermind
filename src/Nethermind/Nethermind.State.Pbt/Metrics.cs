// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.ComponentModel;
using Nethermind.Core.Attributes;
using Nethermind.Core.Collections;
using Nethermind.Core.Metric;
using Nethermind.Pbt;
using NonBlocking;

namespace Nethermind.State.Pbt;

public static class Metrics
{
    [GaugeMetric]
    [Description("Estimated retained PBT trie-cache memory in bytes by partition, including entry and bucket overhead")]
    [KeyIsLabel("partition")]
    public static ConcurrentDictionary<string, long> PbtTrieCacheMemory { get; } = NewTrieCacheMetric();

    [GaugeMetric]
    [Description("Node groups retained in the PBT trie cache by partition")]
    [KeyIsLabel("partition")]
    public static ConcurrentDictionary<string, long> PbtTrieCacheEntries { get; } = NewTrieCacheMetric();

    [CounterMetric]
    [Description("PBT trie-node cache lookups that returned a cached node group, by partition")]
    [KeyIsLabel("partition")]
    public static ConcurrentDictionary<string, long> PbtTrieCacheHits { get; } = NewTrieCacheMetric();

    [CounterMetric]
    [Description("PBT trie-node cache lookups that did not return a cached node group, by partition")]
    [KeyIsLabel("partition")]
    public static ConcurrentDictionary<string, long> PbtTrieCacheMisses { get; } = NewTrieCacheMetric();

    private static ConcurrentDictionary<string, long> NewTrieCacheMetric() => new()
    {
        ["account"] = 0,
        ["code"] = 0,
        ["storage"] = 0,
    };

    internal static readonly PbtSnapshotMemoryLabel AccountLeafSnapshotMemory = new("account", "leaf");
    internal static readonly PbtSnapshotMemoryLabel AccountTrieSnapshotMemory = new("account", "trie");

    [DetailedMetric]
    [Description("Time a pbt write batch was open, covering the block's storage and account flush (Stopwatch ticks)")]
    [ExponentialPowerHistogramMetric(Start = 1000, Factor = 1.5, Count = 40)]
    public static IMetricObserver PbtWriteBatchTime { get; set; } = new NoopMetricObserver();

    [DetailedMetric]
    [Description("Time folding pbt's dirty stems into a new tree root (Stopwatch ticks)")]
    [ExponentialPowerHistogramMetric(Start = 1000, Factor = 1.5, Count = 40)]
    public static IMetricObserver PbtRootHashTime { get; set; } = new NoopMetricObserver();

    /// <remarks>Covers the whole commit: the fold it starts with (<see cref="PbtRootHashTime"/>) and the publish that follows.</remarks>
    [DetailedMetric]
    [Description("Time committing a pbt world state scope, fold included (Stopwatch ticks)")]
    [ExponentialPowerHistogramMetric(Start = 1000, Factor = 1.5, Count = 40)]
    public static IMetricObserver PbtCommitTime { get; set; } = new NoopMetricObserver();

    /// <remarks>Sealing the write buffer into a snapshot and handing it, with the block's transient resource, to the manager.</remarks>
    [DetailedMetric]
    [Description("Time publishing a committed pbt snapshot to the db manager (Stopwatch ticks)")]
    [ExponentialPowerHistogramMetric(Start = 1000, Factor = 1.5, Count = 40)]
    public static IMetricObserver PbtPublishSnapshotTime { get; set; } = new NoopMetricObserver();

    /// <remarks>Dominated by gathering the bundle: every snapshot of the unpersisted chain is leased, so it grows with <see cref="PbtSnapshotBundleSize"/>.</remarks>
    [DetailedMetric]
    [Description("Time opening a pbt world state scope, including gathering its snapshot bundle (Stopwatch ticks)")]
    [ExponentialPowerHistogramMetric(Start = 1000, Factor = 1.5, Count = 40)]
    public static IMetricObserver PbtBeginScopeTime { get; set; } = new NoopMetricObserver();

    [DetailedMetric]
    [Description("Time preparing pbt leaf changes in the world state scope (Stopwatch ticks)")]
    [ExponentialPowerHistogramMetric(Start = 1000, Factor = 1.5, Count = 40)]
    public static IMetricObserver PbtPrepareLeafChangesTime { get; set; } = new NoopMetricObserver();

    [DetailedMetric]
    [Description("Time updating the pbt trie root in the world state scope (Stopwatch ticks)")]
    [ExponentialPowerHistogramMetric(Start = 1000, Factor = 1.5, Count = 40)]
    public static IMetricObserver PbtTrieUpdaterTime { get; set; } = new NoopMetricObserver();

    /// <remarks>The partitions fold concurrently, so the slowest one bounds <see cref="PbtTrieUpdaterTime"/>.</remarks>
    [DetailedMetric]
    [Description("Time folding one pbt partition's dirty stems inside the trie updater, by partition (Stopwatch ticks)")]
    [ExponentialPowerHistogramMetric(Start = 1000, Factor = 1.5, Count = 40, LabelNames = ["partition"])]
    public static IMetricObserver PbtPartitionFoldTime { get; set; } = new NoopMetricObserver();

    [DetailedMetric]
    [Description("Time of a node-group read by the pbt trie updater, by partition (suffixed _top for groups the persistence keeps in the top column) and whether a group was found (Stopwatch ticks)")]
    [ExponentialPowerHistogramMetric(Start = 1, Factor = 1.5, Count = 30, LabelNames = ["partition", "result"])]
    public static IMetricObserver PbtTrieUpdaterNodeGroupReadTimes { get; set; } = new NoopMetricObserver();

    [DetailedMetric]
    [Description("Pbt pooled resources currently rented, by category and type")]
    [KeyIsLabel("category", "resource_type")]
    public static ConcurrentDictionary<PbtResourcePool.PooledResourceLabel, long> PbtActivePooledResource { get; } = new();

    [DetailedMetric]
    [Description("Pbt pooled resources held in the pool, by category and type")]
    [KeyIsLabel("category", "resource_type")]
    public static ConcurrentDictionary<PbtResourcePool.PooledResourceLabel, long> PbtCachedPooledResource { get; } = new();

    /// <remarks>Plateaus once the pool is warm; a category sized too small climbs forever instead.</remarks>
    [DetailedMetric]
    [Description("Pbt pooled resources allocated because the pool was empty, by category and type")]
    [KeyIsLabel("category", "resource_type")]
    public static ConcurrentDictionary<PbtResourcePool.PooledResourceLabel, long> PbtCreatedPooledResource { get; } = new();

    /// <remarks>
    /// One observation per point read of an account, storage slot, node group, or code.
    /// Snapshot hits include tombstones and cleared storage, except node-group tombstones which report as <c>_snapshot_null</c>.
    /// Persistence timings exclude the preceding unsuccessful snapshot walk and distinguish missing values (null or zero storage).
    /// Storage reads of header-embedded slots (below <see cref="PbtKeyDerivation.HeaderStorageOffset"/>) report as <c>storage_header_*</c>.
    /// Whole-run reads, which seed a scope's write buffer before its first write to a run, report as <c>storage_run_*</c> and <c>storage_run_header_*</c>.
    /// </remarks>
    [DetailedMetric]
    [Description("Time of a read through the pbt read-only snapshot bundle, by read type, node-group partition, tier and result (Stopwatch ticks)")]
    [ExponentialPowerHistogramMetric(Start = 1, Factor = 1.5, Count = 30, LabelNames = ["type"])]
    public static IMetricObserver PbtReadOnlySnapshotBundleTimes { get; set; } = new NoopMetricObserver();

    /// <remarks>
    /// Shares the node-group labels of <see cref="PbtReadOnlySnapshotBundleTimes"/>, whose histogram already counts
    /// the reads, so size and count divide into a mean payload per tier and partition. Only a read that found a
    /// group has a size, so the <c>_null</c> tiers of that label set do not appear here.
    /// </remarks>
    [DetailedMetric]
    [Description("Total payload bytes of the node groups read through the pbt read-only snapshot bundle, by node-group partition and tier")]
    [KeyIsLabel("type")]
    public static ConcurrentDictionary<string, long> PbtReadOnlySnapshotBundleNodeGroupBytes { get; } = new()
    {
        ["node_group_account_snapshot"] = 0,
        ["node_group_code_snapshot"] = 0,
        ["node_group_storage_snapshot"] = 0,
        ["node_group_account_persistence"] = 0,
        ["node_group_code_persistence"] = 0,
        ["node_group_storage_persistence"] = 0,
    };

    [GaugeMetric]
    [Description("Retained payload bytes in pbt base snapshots, by partition and value type, excluding tombstones and data-structure overhead")]
    [KeyIsLabel("partition", "type")]
    public static ConcurrentDictionary<PbtSnapshotMemoryLabel, long> PbtBaseSnapshotMemory { get; } = new()
    {
        [AccountLeafSnapshotMemory] = 0,
        [AccountTrieSnapshotMemory] = 0,
    };

    private static long _pbtBaseSnapshotCount;

    [GaugeMetric]
    [Description("Number of pbt base snapshots currently retained in snapshot repositories")]
    public static long PbtBaseSnapshotCount => Volatile.Read(ref _pbtBaseSnapshotCount);

    internal static void AddPbtBaseSnapshot(in PbtSnapshotPayloadSize size, long direction)
    {
        PbtBaseSnapshotMemory.AddBy(AccountLeafSnapshotMemory, direction * size.Leaf);
        PbtBaseSnapshotMemory.AddBy(AccountTrieSnapshotMemory, direction * size.Node);
        Interlocked.Add(ref _pbtBaseSnapshotCount, direction);
    }

    [CounterMetric]
    [Description("Pbt trie-warmer jobs successfully queued, by whether the hint named an address or a storage slot")]
    [KeyIsLabel("kind")]
    public static ConcurrentDictionary<string, long> PbtTrieWarmerTriggered { get; } = NewTrieWarmerMetric();

    [CounterMetric]
    [Description("Pbt trie-warmer hints skipped because the stem was already reserved in the current scope, by whether the hint named an address or a storage slot")]
    [KeyIsLabel("kind")]
    public static ConcurrentDictionary<string, long> PbtTrieWarmerSkippedByDeduplication { get; } = NewTrieWarmerMetric();

    [CounterMetric]
    [Description("Pbt trie-warmer hints lost because the warmer queue refused the job, by whether the hint named an address or a storage slot")]
    [KeyIsLabel("kind")]
    public static ConcurrentDictionary<string, long> PbtTrieWarmerDropped { get; } = NewTrieWarmerMetric();

    [CounterMetric]
    [Description("Pbt trie-warmer jobs skipped at execution because their scope had already committed or closed, by whether the hint named an address or a storage slot")]
    [KeyIsLabel("kind")]
    public static ConcurrentDictionary<string, long> PbtTrieWarmerStale { get; } = NewTrieWarmerMetric();

    [DetailedMetric]
    [Description("Time a pbt trie-warmer job spent walking its path, by whether the hint named an address or a storage slot (Stopwatch ticks)")]
    [ExponentialPowerHistogramMetric(Start = 1, Factor = 1.5, Count = 30, LabelNames = ["kind"])]
    public static IMetricObserver PbtTrieWarmerJobTime { get; set; } = new NoopMetricObserver();

    internal const string TrieWarmerAddressKind = "address";
    internal const string TrieWarmerStorageKind = "storage";

    private static ConcurrentDictionary<string, long> NewTrieWarmerMetric() => new()
    {
        [TrieWarmerAddressKind] = 0,
        [TrieWarmerStorageKind] = 0,
    };

    [GaugeMetric]
    [Description("Number of layers in the most recently assembled pbt read-only snapshot bundle")]
    public static long PbtSnapshotBundleSize { get; set; }

    /// <remarks>Layers widen as they compact, so this diverges from the layer count as compaction runs.</remarks>
    [DetailedMetric]
    [Description("Block-number span covered by the layers of a newly assembled pbt read-only snapshot bundle")]
    [ExponentialPowerHistogramMetric(Start = 1, Factor = 1.5, Count = 30)]
    public static IMetricObserver PbtSnapshotBundleBlockNumberDepth { get; set; } = new NoopMetricObserver();
}

/// <summary>Metric labels identifying a PBT partition and value type.</summary>
public readonly record struct PbtSnapshotMemoryLabel(string Partition, string Type) : IMetricLabels
{
    /// <inheritdoc/>
    public string[] Labels => [Partition, Type];
}

/// <summary>Metric labels identifying a PBT partition and whether a node-group read found a group.</summary>
public readonly record struct PbtNodeGroupReadLabel(string Partition, string Result) : IMetricLabels
{
    /// <inheritdoc/>
    public string[] Labels => [Partition, Result];
}
