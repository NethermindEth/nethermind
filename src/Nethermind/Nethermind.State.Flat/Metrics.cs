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
}
