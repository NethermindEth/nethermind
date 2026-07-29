// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.ComponentModel;
using Nethermind.Core.Attributes;

namespace Nethermind.History;

public static class Metrics
{
    [GaugeMetric]
    [Description("The number of the oldest block stored.")]
    public static ulong OldestStoredBlockNumber { get; set; }

    [GaugeMetric]
    [Description("The number of the oldest block access list stored.")]
    public static ulong? OldestStoredBlockAccessListBlockNumber { get; set; }

    [CounterMetric]
    [Description("The number of the historical blocks that have been pruned (since restart).")]
    public static long BlocksPruned { get; set; }

    [CounterMetric]
    [Description("The number of the historical block access lists that have been pruned (since restart).")]
    public static long BlockAccessListsPruned { get; set; }

    [GaugeMetric]
    [Description("The cutoff block number from which historical blocks will be pruned.")]
    public static ulong? PruningCutoffBlocknumber { get; set; }

    [GaugeMetric]
    [Description("The cutoff block number from which historical block access lists will be pruned.")]
    public static ulong? BlockAccessListPruningCutoffBlocknumber { get; set; }
}
