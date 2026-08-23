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
    [Description("The number of historical block heights reclaimed by pruning (since restart). Heights, not stored blocks: a range is dropped in one operation and never learns how many of its heights held one.")]
    public static long BlockHeightsReclaimed { get; set; }

    [CounterMetric]
    [Description("The number of historical block access list heights reclaimed by pruning (since restart). Heights, not stored access lists - see BlockHeightsReclaimed.")]
    public static long BlockAccessListHeightsReclaimed { get; set; }

    [CounterMetric]
    [Description("The number of transaction index entries dropped because the block they name is no longer retained (since restart).")]
    public static long TransactionIndexEntriesPruned { get; set; }

    [CounterMetric]
    [Description("The number of historical blocks whose receipts were retained past the pruning cutoff because their logs matched a configured slice address (since restart).")]
    public static long SlicedReceiptsRetained { get; set; }

    [GaugeMetric]
    [Description("The cutoff block number from which historical blocks will be pruned.")]
    public static ulong? PruningCutoffBlocknumber { get; set; }

    [GaugeMetric]
    [Description("The cutoff block number from which historical block access lists will be pruned.")]
    public static ulong? BlockAccessListPruningCutoffBlocknumber { get; set; }
}
