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
}
