// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.ComponentModel;
using Nethermind.Core.Attributes;
using Nethermind.Core.Metric;
using NonBlocking;


namespace Nethermind.State.Flat;

public static class Metrics
{
    [GaugeMetric]
    [Description("Average snapshot bundle size in terms of num of snapshot")]
    public static long SnapshotBundleSize { get; set; }

    [GaugeMetric]
    [Description("Average snapshot bundle size in terms of num of snapshot")]
    public static long SnapshotBundlePersistedSnapshotSize { get; set; }

    [DetailedMetric]
    [Description("Time for persistence job")]
    [ExponentialPowerHistogramMetric(Start = 1, Factor = 1.5, Count = 30)]
    public static IMetricObserver FlatPersistenceTime { get; set; } = new NoopMetricObserver();

    [DetailedMetric]
    [Description("Persistence write size")]
    [ExponentialPowerHistogramMetric(Start = 1, Factor = 1.5, Count = 30, LabelNames = ["payload"])]
    public static IMetricObserver FlatPersistenceSnapshotSize { get; set; } = new NoopMetricObserver();

    [DetailedMetric]
    [CounterMetric]
    [Description("Importer entries count")]
    public static long ImporterEntriesCount { get; set; }

    [DetailedMetric]
    [CounterMetric]
    [Description("Importer entries count flat")]
    public static long ImporterEntriesCountFlat { get; set; }

    [GaugeMetric]
    [Description("Active snapshot bundles")]
    public static long ActiveSnapshotBundle { get; set; }

    [GaugeMetric]
    [Description("Number of snapshots")]
    public static long SnapshotCount { get; set; }

    [GaugeMetric]
    [Description("Number of compacted snapshots")]
    public static long CompactedSnapshotCount { get; set; }

    [GaugeMetric]
    [Description("Estimated memory used by snapshots in bytes")]
    public static long SnapshotMemory { get; set; }

    [GaugeMetric]
    [Description("Estimated memory used by compacted snapshot dictionaries in bytes")]
    public static long CompactedSnapshotMemory { get; set; }

    [GaugeMetric]
    [Description("Total estimated snapshot memory in bytes")]
    public static long TotalSnapshotMemory { get; set; }

    [DetailedMetric]
    [Description("Active pooled resources by category and type")]
    [KeyIsLabel("category", "resource_type")]
    public static ConcurrentDictionary<ResourcePool.PooledResourceLabel, long> ActivePooledResource { get; } = new();

    [DetailedMetric]
    [Description("Cached pooled resources by category and type")]
    [KeyIsLabel("category", "resource_type")]
    public static ConcurrentDictionary<ResourcePool.PooledResourceLabel, long> CachedPooledResource { get; } = new();

    [DetailedMetric]
    [Description("Created pooled resources by category and type")]
    [KeyIsLabel("category", "resource_type")]
    public static ConcurrentDictionary<ResourcePool.PooledResourceLabel, long> CreatedPooledResource { get; } = new();

    [DetailedMetric]
    [Description("Readonly snapshot bundle times")]
    [ExponentialPowerHistogramMetric(Start = 1, Factor = 1.5, Count = 30, LabelNames = ["type"])]
    public static IMetricObserver ReadOnlySnapshotBundleTimes { get; set; } = new NoopMetricObserver();

    [DetailedMetric]
    [Description("Time spend compacting snapshots")]
    [ExponentialPowerHistogramMetric(Start = 1, Factor = 1.5, Count = 1, LabelNames = [])]
    public static IMetricObserver CompactTime { get; set; } = new NoopMetricObserver();

    // --- Persisted snapshot metrics ---

    [GaugeMetric]
    [Description("Number of persisted snapshots on disk")]
    public static long PersistedSnapshotCount { get; set; }

    [GaugeMetric]
    [Description("Estimated disk usage of persisted snapshots in bytes")]
    public static long PersistedSnapshotDiskBytes { get; set; }

    [GaugeMetric]
    [Description("Estimated memory used by base persisted snapshots in bytes")]
    public static long PersistedSnapshotMemory { get; set; }

    [GaugeMetric]
    [Description("Estimated memory used by compacted persisted snapshots in bytes")]
    public static long CompactedPersistedSnapshotMemory { get; set; }

    // Backed by fields so callers can update via Interlocked.Add(ref ...).
    internal static long _persistedSnapshotKeyBloomMemory;
    internal static long _persistedSnapshotTrieBloomMemory;

    [GaugeMetric]
    [Description("Memory used by per-snapshot key bloom filters (address/slot/self-destruct) in bytes")]
    public static long PersistedSnapshotKeyBloomMemory
    {
        get => Volatile.Read(ref _persistedSnapshotKeyBloomMemory);
        set => Volatile.Write(ref _persistedSnapshotKeyBloomMemory, value);
    }

    [GaugeMetric]
    [Description("Memory used by per-snapshot trie bloom filters (state and storage trie nodes) in bytes")]
    public static long PersistedSnapshotTrieBloomMemory
    {
        get => Volatile.Read(ref _persistedSnapshotTrieBloomMemory);
        set => Volatile.Write(ref _persistedSnapshotTrieBloomMemory, value);
    }

    [DetailedMetric]
    [CounterMetric]
    [Description("Number of persisted snapshot compactions performed")]
    public static long PersistedSnapshotCompactions { get; set; }

    [DetailedMetric]
    [CounterMetric]
    [Description("Number of persisted snapshot file writes")]
    public static long PersistedSnapshotWrites { get; set; }

    [DetailedMetric]
    [CounterMetric]
    [Description("Number of persisted snapshot prunes")]
    public static long PersistedSnapshotPrunes { get; set; }

    // Push-style gauges: ArenaManager increments/decrements these on every file add, remove,
    // and resize. Keyed by the typed PersistedSnapshotTier singleton so the small and large
    // arena pools surface separately in Prometheus; the metrics controller dispatches on
    // IMetricLabels to produce the wire-format "small"/"large" label.
    [Description("Number of arena files backing persisted snapshots, by tier")]
    [KeyIsLabel("tier")]
    public static ConcurrentDictionary<PersistedSnapshotTier, long> ArenaFileCountByTier { get; } = new();

    [Description("Total mmap size of arena files backing persisted snapshots in bytes, by tier")]
    [KeyIsLabel("tier")]
    public static ConcurrentDictionary<PersistedSnapshotTier, long> ArenaMappedBytesByTier { get; } = new();

    [DetailedMetric]
    [Description("Live arena reservations by tag")]
    [KeyIsLabel("tag")]
    public static ConcurrentDictionary<string, long> ArenaReservationCountByTag { get; } = new();

    [DetailedMetric]
    [Description("Live arena reservation bytes by tag")]
    [KeyIsLabel("tag")]
    public static ConcurrentDictionary<string, long> ArenaReservationBytesByTag { get; } = new();
}
