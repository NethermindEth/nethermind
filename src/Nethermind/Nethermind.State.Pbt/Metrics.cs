// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.ComponentModel;
using Nethermind.Core.Attributes;
using Nethermind.Core.Collections;
using Nethermind.Core.Metric;
using Nethermind.Pbt;
using Nethermind.State.Flat;
using Nethermind.State.Pbt.Snapshot;
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
    [Description("Time sealing a committed pbt snapshot and adding it to the db manager (Stopwatch ticks)")]
    [ExponentialPowerHistogramMetric(Start = 1000, Factor = 1.5, Count = 40)]
    public static IMetricObserver PbtAddSnapshotTime { get; set; } = new NoopMetricObserver();

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
    public static ConcurrentDictionary<ResourcePool.PooledResourceLabel, long> PbtActivePooledResource { get; } = new();

    [DetailedMetric]
    [Description("Pbt pooled resources held in the pool, by category and type")]
    [KeyIsLabel("category", "resource_type")]
    public static ConcurrentDictionary<ResourcePool.PooledResourceLabel, long> PbtCachedPooledResource { get; } = new();

    /// <remarks>Plateaus once the pool is warm; a category sized too small climbs forever instead.</remarks>
    [DetailedMetric]
    [Description("Pbt pooled resources allocated because the pool was empty, by category and type")]
    [KeyIsLabel("category", "resource_type")]
    public static ConcurrentDictionary<ResourcePool.PooledResourceLabel, long> PbtCreatedPooledResource { get; } = new();

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

    private static long _pbtInMemorySlotFilterMemory;

    [GaugeMetric]
    [Description("Memory held by the negative filters over the pbt in-memory snapshots' slot runs in bytes; one filter per read-only snapshot bundle that served a read-only execution slot read")]
    public static long PbtInMemorySlotFilterMemory => Volatile.Read(ref _pbtInMemorySlotFilterMemory);

    private static long _pbtInMemorySlotFilterBuilds;

    [CounterMetric]
    [Description("Negative filters built over the pbt in-memory snapshots' slot runs")]
    public static long PbtInMemorySlotFilterBuilds => Volatile.Read(ref _pbtInMemorySlotFilterBuilds);

    private static long _pbtInMemorySlotFilterBuildFailures;

    [CounterMetric]
    [Description("Negative filters over the pbt in-memory snapshots' slot runs that failed to build; their bundles read slots without a filter")]
    public static long PbtInMemorySlotFilterBuildFailures => Volatile.Read(ref _pbtInMemorySlotFilterBuildFailures);

    [DetailedMetric]
    [Description("Time to build the negative filter over the pbt in-memory snapshots' slot runs (Stopwatch ticks)")]
    [ExponentialPowerHistogramMetric(Start = 1, Factor = 1.5, Count = 40)]
    public static IMetricObserver PbtInMemorySlotFilterBuildTime { get; set; } = new NoopMetricObserver();

    internal static void RecordPbtInMemorySlotFilterBuilt(long bytes, long elapsedTicks)
    {
        Interlocked.Add(ref _pbtInMemorySlotFilterMemory, bytes);
        Interlocked.Increment(ref _pbtInMemorySlotFilterBuilds);
        PbtInMemorySlotFilterBuildTime.Observe(elapsedTicks);
    }

    internal static void RecordPbtInMemorySlotFilterBuildFailed() => Interlocked.Increment(ref _pbtInMemorySlotFilterBuildFailures);

    internal static void RecordPbtInMemorySlotFilterReleased(long bytes) => Interlocked.Add(ref _pbtInMemorySlotFilterMemory, -bytes);

    [GaugeMetric]
    [DetailedMetric]
    [Description("Retained payload bytes in pbt base snapshots, by value type, excluding tombstones and data-structure overhead")]
    [KeyIsLabel("type")]
    public static ConcurrentDictionary<string, long> PbtBaseSnapshotMemory { get; } = new()
    {
        ["leaf"] = 0,
        ["trie"] = 0,
    };

    private static long _pbtBaseSnapshotCount;

    [GaugeMetric]
    [Description("Number of pbt base snapshots currently retained in snapshot repositories")]
    public static long PbtBaseSnapshotCount => Volatile.Read(ref _pbtBaseSnapshotCount);

    internal static void AddPbtBaseSnapshotCount(long delta) => Interlocked.Add(ref _pbtBaseSnapshotCount, delta);

    internal static void AddPbtBaseSnapshotMemory(in PbtSnapshotPayloadSize size, long direction)
    {
        PbtBaseSnapshotMemory.AddBy("leaf", direction * size.Leaf);
        PbtBaseSnapshotMemory.AddBy("trie", direction * size.Node);
    }

    [GaugeMetric]
    [Description("Number of layers in the most recently assembled pbt read-only snapshot bundle")]
    public static long PbtSnapshotBundleSize { get; set; }

    /// <remarks>Layers widen as they compact, so this diverges from the layer count as compaction runs.</remarks>
    [DetailedMetric]
    [Description("Block-number span covered by the layers of a newly assembled pbt read-only snapshot bundle")]
    [ExponentialPowerHistogramMetric(Start = 1, Factor = 1.5, Count = 30)]
    public static IMetricObserver PbtSnapshotBundleBlockNumberDepth { get; set; } = new NoopMetricObserver();

    [CounterMetric]
    [Description("Key and value bytes merged from the pbt trie node log into RocksDB (the latest record per key of each generation), by column (account, storage)")]
    [KeyIsLabel("column")]
    public static ConcurrentDictionary<string, long> PbtTrieNodeLogFlushedBytes { get; } = new();

    [CounterMetric]
    [Description("Node-group reads answered by the pbt trie node log: hit (served from the log), chain (served after walking to an older version), miss (fell through to the second-level log if enabled, else RocksDB), and the same outcomes of the second-level log prefixed second_level_")]
    [KeyIsLabel("outcome")]
    public static ConcurrentDictionary<string, long> PbtTrieNodeLogReads { get; } = new();

    private static long _pbtTrieNodeLogIndexFalseMatches;

    [CounterMetric]
    [Description("Pbt trie node log index probes whose slot tag matched but whose record held another key, so the record was read for nothing")]
    public static long PbtTrieNodeLogIndexFalseMatches => Volatile.Read(ref _pbtTrieNodeLogIndexFalseMatches);

    public static void IncrementPbtTrieNodeLogIndexFalseMatches() => Interlocked.Increment(ref _pbtTrieNodeLogIndexFalseMatches);

    [CounterMetric]
    [Description("Bytes written to first-level pbt trie node log files (records with their headers), by column (account, storage)")]
    [KeyIsLabel("column")]
    public static ConcurrentDictionary<string, long> PbtTrieNodeLogStoredBytes { get; } = new();

    [CounterMetric]
    [Description("Bytes copied from merged first-level generations into second-level pbt trie node log files (records with their headers), by column (account, storage)")]
    [KeyIsLabel("column")]
    public static ConcurrentDictionary<string, long> PbtTrieNodeLogSecondLevelStoredBytes { get; } = new();

    [DetailedMetric]
    [Description("Time to merge one pbt trie node log generation into RocksDB")]
    [ExponentialPowerHistogramMetric(Start = 1, Factor = 1.5, Count = 30)]
    public static IMetricObserver PbtTrieNodeLogMergeTime { get; set; } = new NoopMetricObserver();

    [DetailedMetric]
    [Description("Time a pbt trie node log shard waited for a merge before starting a new generation because its backlog of unmerged generations was full; persistence stalls for this long")]
    [ExponentialPowerHistogramMetric(Start = 1, Factor = 1.5, Count = 30)]
    public static IMetricObserver PbtTrieNodeLogBackpressureTime { get; set; } = new NoopMetricObserver();

    [DetailedMetric]
    [Description("Time to commit one batch to the pbt trie node log: buffer flush, fsync and index publish")]
    [ExponentialPowerHistogramMetric(Start = 1, Factor = 1.5, Count = 30)]
    public static IMetricObserver PbtTrieNodeLogCommitTime { get; set; } = new NoopMetricObserver();

    [GaugeMetric]
    [Description("Pbt trie node log generations by state: active (being appended to), sealed (waiting for or being merged), merged_pinned (merged into RocksDB but still held open by a reader)")]
    [KeyIsLabel("state")]
    public static ConcurrentDictionary<string, long> PbtTrieNodeLogGenerationCount { get; } = new();

    private static long _pbtTrieNodeLogBytes;

    [GaugeMetric]
    [Description("Bytes of pbt trie node log generation files not yet merged into RocksDB")]
    public static long PbtTrieNodeLogBytes => Volatile.Read(ref _pbtTrieNodeLogBytes);

    public static void AddPbtTrieNodeLogBytes(long delta) => Interlocked.Add(ref _pbtTrieNodeLogBytes, delta);

    [GaugeMetric]
    [Description("Native memory held by pbt trie node log generation indexes, including merged generations still pinned by readers")]
    public static long PbtTrieNodeLogIndexBytes { get; set; }

    [GaugeMetric]
    [Description("Version of the last batch committed to the pbt trie node log, by shard")]
    [KeyIsLabel("shard")]
    public static ConcurrentDictionary<string, long> PbtTrieNodeLogVersion { get; } = new();

    [GaugeMetric]
    [Description("Newest pbt trie node log generation merged into RocksDB, by shard")]
    [KeyIsLabel("shard")]
    public static ConcurrentDictionary<string, long> PbtTrieNodeLogFlushedGeneration { get; } = new();
}

/// <summary>Metric labels identifying a PBT partition and whether a node-group read found a group.</summary>
public readonly record struct PbtNodeGroupReadLabel(string Partition, string Result) : IMetricLabels
{
    /// <inheritdoc/>
    public string[] Labels => [Partition, Result];
}
