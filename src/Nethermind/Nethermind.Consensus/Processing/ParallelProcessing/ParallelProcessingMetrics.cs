// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.ComponentModel;
using System.Threading;
using Nethermind.Core.Attributes;

namespace Nethermind.Consensus.Processing.ParallelProcessing;

public static class ParallelProcessingMetrics
{
    [CounterMetric]
    [Description("Total number of transaction re-executions due to read conflicts in parallel block processing.")]
    public static long Reexecutions;

    [CounterMetric]
    [Description("Total number of transaction re-validations in parallel block processing.")]
    public static long Revalidations;

    [CounterMetric]
    [Description("Total number of blocked reads waiting for prior transactions in parallel block processing.")]
    public static long BlockedReads;

    [CounterMetric]
    [Description("Total number of transactions processed in parallel blocks.")]
    public static long TxCount;

    [GaugeMetric]
    [Description("Number of re-executions in the last parallel block.")]
    public static long LastBlockReexecutions;

    [GaugeMetric]
    [Description("Number of re-validations in the last parallel block.")]
    public static long LastBlockRevalidations;

    [GaugeMetric]
    [Description("Number of blocked reads in the last parallel block.")]
    public static long LastBlockBlockedReads;

    [GaugeMetric]
    [Description("Number of transactions in the last parallel block.")]
    public static long LastBlockTxCount;

    public static void IncrementReexecutions() => Interlocked.Increment(ref Reexecutions);
    public static void IncrementRevalidations() => Interlocked.Increment(ref Revalidations);
    public static void IncrementBlockedReads() => Interlocked.Increment(ref BlockedReads);

    public static void ReportBlock(int txCount, long reexecutions, long revalidations, long blockedReads)
    {
        Interlocked.Add(ref TxCount, txCount);
        LastBlockTxCount = txCount;
        LastBlockReexecutions = reexecutions;
        LastBlockRevalidations = revalidations;
        LastBlockBlockedReads = blockedReads;
    }
}
