// SPDX-FileCopyrightText: 20225Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.ComponentModel;
using Nethermind.Core.Attributes;

namespace Nethermind.History;

public static class Metrics
{
    [GaugeMetric]
    [Description("The number of the oldest block stored.")]
    public static long OldestStoredBlockNumber { get; set; }

    [CounterMetric]
    [Description("The number of the historical blocks that have been pruned (since restart).")]
    public static long BlocksPruned { get; set; }

    [GaugeMetric]
    [Description("The cutoff timestamp from which historical blocks will be pruned.")]
    public static ulong? PruningCutoffTimestamp { get; set; }

    [GaugeMetric]
    [Description("The cutoff block number from which historical blocks will be pruned.")]
    public static long? PruningCutoffBlocknumber { get; set; }
}
