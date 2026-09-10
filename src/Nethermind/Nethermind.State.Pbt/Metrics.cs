// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.ComponentModel;
using Nethermind.Core.Attributes;
using Nethermind.Core.Collections;
using Nethermind.Core.Metric;
using NonBlocking;

namespace Nethermind.State.Pbt;

public static class Metrics
{
    internal static readonly PbtSnapshotMemoryLabel AccountLeafSnapshotMemory = new("account", "leaf");
    internal static readonly PbtSnapshotMemoryLabel AccountTrieSnapshotMemory = new("account", "trie");
    internal static readonly PbtSnapshotMemoryLabel CodeLeafSnapshotMemory = new("code", "leaf");
    internal static readonly PbtSnapshotMemoryLabel CodeTrieSnapshotMemory = new("code", "trie");
    internal static readonly PbtSnapshotMemoryLabel StorageLeafSnapshotMemory = new("storage", "leaf");
    internal static readonly PbtSnapshotMemoryLabel StorageTrieSnapshotMemory = new("storage", "trie");

    [DetailedMetric]
    [Description("Time a pbt write batch was open, covering the block's storage and account flush (Stopwatch ticks)")]
    [ExponentialPowerHistogramMetric(Start = 1000, Factor = 1.5, Count = 40)]
    public static IMetricObserver PbtWriteBatchTime { get; set; } = new NoopMetricObserver();

    [DetailedMetric]
    [Description("Time folding pbt's dirty stems into a new tree root (Stopwatch ticks)")]
    [ExponentialPowerHistogramMetric(Start = 1000, Factor = 1.5, Count = 40)]
    public static IMetricObserver PbtRootHashTime { get; set; } = new NoopMetricObserver();

    [DetailedMetric]
    [Description("Time preparing pbt leaf changes in the world state scope (Stopwatch ticks)")]
    [ExponentialPowerHistogramMetric(Start = 1000, Factor = 1.5, Count = 40)]
    public static IMetricObserver PbtPrepareLeafChangesTime { get; set; } = new NoopMetricObserver();

    [DetailedMetric]
    [Description("Time updating the pbt trie root in the world state scope (Stopwatch ticks)")]
    [ExponentialPowerHistogramMetric(Start = 1000, Factor = 1.5, Count = 40)]
    public static IMetricObserver PbtTrieUpdaterTime { get; set; } = new NoopMetricObserver();

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
    /// One observation per point read of an account, storage slot, node group, code, or code reference.
    /// Snapshot hits include tombstones and cleared storage. Persistence timings exclude the preceding
    /// unsuccessful snapshot walk and distinguish missing values (null, zero storage, or zero references).
    /// </remarks>
    [DetailedMetric]
    [Description("Time of a read through the pbt read-only snapshot bundle, by read type, tier and result (Stopwatch ticks)")]
    [ExponentialPowerHistogramMetric(Start = 1, Factor = 1.5, Count = 30, LabelNames = ["type"])]
    public static IMetricObserver PbtReadOnlySnapshotBundleTimes { get; set; } = new NoopMetricObserver();

    [GaugeMetric]
    [Description("Retained payload bytes in pbt base snapshots, by partition and value type, excluding tombstones and data-structure overhead")]
    [KeyIsLabel("partition", "type")]
    public static ConcurrentDictionary<PbtSnapshotMemoryLabel, long> PbtBaseSnapshotMemory { get; } = new()
    {
        [AccountLeafSnapshotMemory] = 0,
        [AccountTrieSnapshotMemory] = 0,
        [CodeLeafSnapshotMemory] = 0,
        [CodeTrieSnapshotMemory] = 0,
        [StorageLeafSnapshotMemory] = 0,
        [StorageTrieSnapshotMemory] = 0,
    };

    private static long _pbtBaseSnapshotCount;

    [GaugeMetric]
    [Description("Number of pbt base snapshots currently retained in snapshot repositories")]
    public static long PbtBaseSnapshotCount => Volatile.Read(ref _pbtBaseSnapshotCount);

    internal static void AddPbtBaseSnapshot(in PbtSnapshotPayloadSize size, long direction)
    {
        PbtBaseSnapshotMemory.AddBy(AccountLeafSnapshotMemory, direction * size.Leaf);
        PbtBaseSnapshotMemory.AddBy(AccountTrieSnapshotMemory, direction * size.Node);
        PbtBaseSnapshotMemory.AddBy(CodeLeafSnapshotMemory, direction * size.CodeReference);
        Interlocked.Add(ref _pbtBaseSnapshotCount, direction);
    }

    private static long _pbtTrieWarmerTriggered;

    [CounterMetric]
    [Description("Pbt trie-warmer jobs successfully queued")]
    public static long PbtTrieWarmerTriggered => Volatile.Read(ref _pbtTrieWarmerTriggered);

    internal static void IncrementPbtTrieWarmerTriggered() => Interlocked.Increment(ref _pbtTrieWarmerTriggered);

    private static long _pbtTrieWarmerSkippedByDeduplication;

    [CounterMetric]
    [Description("Pbt trie-warmer hints skipped because the stem was already reserved in the current scope")]
    public static long PbtTrieWarmerSkippedByDeduplication => Volatile.Read(ref _pbtTrieWarmerSkippedByDeduplication);

    internal static void IncrementPbtTrieWarmerSkippedByDeduplication() => Interlocked.Increment(ref _pbtTrieWarmerSkippedByDeduplication);

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
