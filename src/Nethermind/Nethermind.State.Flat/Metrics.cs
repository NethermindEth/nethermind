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
    public static double SnapshotBundleSize { get; set; }

    [DetailedMetric]
    [Description("Time for persistence job")]
    [ExponentialPowerHistogramMetric(Start = 1, Factor = 1.5, Count = 30)]
    public static IMetricObserver FlatPersistenceTime { get; set; } = new NoopMetricObserver();

    [DetailedMetric]
    [Description("Persistence write size")]
    [ExponentialPowerHistogramMetric(Start = 1, Factor = 1.5, Count = 30, LabelNames = ["payload"])]
    public static IMetricObserver FlatPersistenceSnapshotSize { get; set; } = new NoopMetricObserver();

    [DetailedMetric]
    [Description("Persistence write size")]
    [KeyIsLabel("category")]
    public static ConcurrentDictionary<MemoryTypeMetric, long> SnapshotsMemory { get; } = new ConcurrentDictionary<MemoryTypeMetric, long>();

    [CounterMetric]
    [Description("Importer entries count")]
    public static long ImporterEntriesCount { get; set; }

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

    // === Gauges with single label ===
    [DetailedMetric]
    [Description("Compacted snapshot memory by category")]
    [KeyIsLabel("category")]
    public static ConcurrentDictionary<MemoryTypeMetric, long> CompactedMemory { get; } = new();

    [DetailedMetric]
    [Description("Active snapshot content by category")]
    [KeyIsLabel("category")]
    public static ConcurrentDictionary<StringLabel, long> ActiveSnapshotContent { get; } = new();

    [DetailedMetric]
    [Description("Cached snapshot content by category")]
    [KeyIsLabel("category")]
    public static ConcurrentDictionary<StringLabel, long> CachedSnapshotContent { get; } = new();

    [DetailedMetric]
    [Description("Pool full snapshot content by category")]
    [KeyIsLabel("category")]
    public static ConcurrentDictionary<StringLabel, long> PoolFullSnapshotContent { get; } = new();

    [DetailedMetric]
    [Description("Active cached resource by category")]
    [KeyIsLabel("category")]
    public static ConcurrentDictionary<StringLabel, long> ActiveCachedResource { get; } = new();

    [DetailedMetric]
    [Description("Cached cached resource by category")]
    [KeyIsLabel("category")]
    public static ConcurrentDictionary<StringLabel, long> CachedCachedResource { get; } = new();

    [DetailedMetric]
    [Description("Pool full cached resource by category")]
    [KeyIsLabel("category")]
    public static ConcurrentDictionary<StringLabel, long> PoolFullCachedResource { get; } = new();

    // === Counters with labels ===
    [DetailedMetric]
    [Description("Created snapshot content count")]
    [KeyIsLabel("compacted")]
    public static ConcurrentDictionary<StringLabel, long> CreatedSnapshotContent { get; } = new();

    [DetailedMetric]
    [Description("Created cached resource count")]
    [KeyIsLabel("compacted")]
    public static ConcurrentDictionary<StringLabel, long> CreatedCachedResource { get; } = new();

    [DetailedMetric]
    [Description("Snapshot bundle events")]
    [KeyIsLabel("type", "is_prewarmer")]
    public static ConcurrentDictionary<TwoStringLabel, long> SnapshotBundleEvents { get; } = new();

    // === Histograms ===
    [DetailedMetric]
    [Description("Flat diff operation times")]
    [ExponentialPowerHistogramMetric(Start = 1, Factor = 1.5, Count = 30, LabelNames = ["category", "type"])]
    public static IMetricObserver FlatDiffTimes { get; set; } = new NoopMetricObserver();

    [DetailedMetric]
    [Description("Snapshot bundle result size")]
    [ExponentialPowerHistogramMetric(Start = 1, Factor = 1.5, Count = 30, LabelNames = ["type"])]
    public static IMetricObserver SnapshotBundleResultSize { get; set; } = new NoopMetricObserver();

    [DetailedMetric]
    [Description("Snapshot bundle times")]
    [ExponentialPowerHistogramMetric(Start = 1, Factor = 1.5, Count = 30, LabelNames = ["type", "is_prewarmer"])]
    public static IMetricObserver SnapshotBundleTimes { get; set; } = new NoopMetricObserver();

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
