// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.ComponentModel;
using Nethermind.Core.Attributes;
using Nethermind.Core.Metric;
using NonBlocking;

using StringLabel = Nethermind.Core.Attributes.StringLabel;

namespace Nethermind.State.Flat;

public static class Metrics
{
    [GaugeMetric]
    [Description("Average snapshot bundle size in terms of num of snapshot")]
    public static long SnapshotBundleSize { get; set; }

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

    [DetailedMetric]
    [Description("Time spend compaction snapshots for mid compaction")]
    [ExponentialPowerHistogramMetric(Start = 1, Factor = 1.5, Count = 1, LabelNames = [])]
    public static IMetricObserver MidCompactTime { get; set; } = new NoopMetricObserver();
}
