// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.ComponentModel;
using Nethermind.Core.Attributes;
using Nethermind.Core.Metric;
using NonBlocking;

namespace Nethermind.State.Pbt;

public static class Metrics
{
    /// <remarks>
    /// One observation per block: the batch is opened once per committing world state and spans the
    /// whole flush of the block's dirty storage and accounts into the tree.
    /// </remarks>
    [DetailedMetric]
    [Description("Time a pbt write batch was open, covering the block's storage and account flush (Stopwatch ticks)")]
    [ExponentialPowerHistogramMetric(Start = 1000, Factor = 1.5, Count = 40)]
    public static IMetricObserver PbtWriteBatchTime { get; set; } = new NoopMetricObserver();

    /// <remarks>
    /// Only folds that had something to do are observed, so this counts root updates rather than calls.
    /// </remarks>
    [DetailedMetric]
    [Description("Time folding pbt's dirty stems into a new tree root (Stopwatch ticks)")]
    [ExponentialPowerHistogramMetric(Start = 1000, Factor = 1.5, Count = 40)]
    public static IMetricObserver PbtRootHashTime { get; set; } = new NoopMetricObserver();

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
    /// One observation per read, labelled by the tier that answered it: a layer-chain hit, or the
    /// persistence reader below it, split by whether it had a value. That split matters because an
    /// absent value costs a full walk plus a database miss, which is the expensive shape. A trie node
    /// read reaching persistence is split further by the zone partition it is keyed into, the three
    /// columns differing enough in size and write rate to be worth telling apart.
    /// <para>
    /// An account or slot read reaching persistence also observes the leaf fetch alone, under a
    /// <c>_fetch</c> label, from the same start as the total: the two nest rather than partition, so the
    /// decode is what the total leaves over the fetch.
    /// </para>
    /// </remarks>
    [DetailedMetric]
    [Description("Time of a read through the pbt read-only snapshot bundle, by tier and result, and by partition for a persisted trie node (Stopwatch ticks)")]
    [ExponentialPowerHistogramMetric(Start = 1, Factor = 1.5, Count = 30, LabelNames = ["type"])]
    public static IMetricObserver PbtReadOnlySnapshotBundleTimes { get; set; } = new NoopMetricObserver();

    /// <remarks>
    /// Counted only where detailed metrics are on, and only for the reads that got past the tiers above
    /// the cache: a hit is a leaf blob the block had already read out of the shared view, and a miss is
    /// one that had to be read from it. Both are per read, so the ratio is the cache's hit rate.
    /// </remarks>
    [DetailedMetric]
    [CounterMetric]
    [Description("Reads served by a pbt bundle's leaf blob cache")]
    public static long PbtLeafBlobCacheHits { get; set; }

    /// <inheritdoc cref="PbtLeafBlobCacheHits"/>
    [DetailedMetric]
    [CounterMetric]
    [Description("Reads that missed a pbt bundle's leaf blob cache and went to the shared view")]
    public static long PbtLeafBlobCacheMisses { get; set; }

    [GaugeMetric]
    [Description("Number of layers in the most recently assembled pbt read-only snapshot bundle")]
    public static long PbtSnapshotBundleSize { get; set; }

    /// <remarks>Layers widen as they compact, so this diverges from the layer count as compaction runs.</remarks>
    [DetailedMetric]
    [Description("Block-number span covered by the layers of a newly assembled pbt read-only snapshot bundle")]
    [ExponentialPowerHistogramMetric(Start = 1, Factor = 1.5, Count = 30)]
    public static IMetricObserver PbtSnapshotBundleBlockNumberDepth { get; set; } = new NoopMetricObserver();
}
