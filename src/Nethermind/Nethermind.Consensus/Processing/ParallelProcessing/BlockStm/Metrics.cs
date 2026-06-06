// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.ComponentModel;
using System.Threading;
using Nethermind.Core.Attributes;

namespace Nethermind.Consensus.Processing.ParallelProcessing.BlockStm;

public static class Metrics
{
    [CounterMetric]
    [Description("Total number of re-execution attempts in parallel block processing.")]
    public static long Reexecutions;

    [CounterMetric]
    [Description("Total number of validation aborts in parallel block processing.")]
    public static long Revalidations;

    [CounterMetric]
    [Description("Total number of blocked read aborts in parallel block processing.")]
    public static long BlockedReads;

    [CounterMetric]
    [Description("Total number of transactions processed in parallel blocks.")]
    public static long TxCount;

    [GaugeMetric]
    [Description("Number of re-execution attempts in the last parallel block.")]
    public static long LastBlockReexecutions;

    [GaugeMetric]
    [Description("Number of validation aborts in the last parallel block.")]
    public static long LastBlockRevalidations;

    [GaugeMetric]
    [Description("Number of blocked read aborts in the last parallel block.")]
    public static long LastBlockBlockedReads;

    [GaugeMetric]
    [Description("Number of transactions in the last parallel block.")]
    public static long LastBlockTxCount;

    [GaugeMetric]
    [Description("Percent of transactions executed without re-execution in the last parallel block.")]
    public static long LastBlockParallelizationPercent;

    internal static void ReportBlock(in ParallelBlockMetrics snapshot)
    {
        // Gauges: monitoring threads read these asynchronously, so a slightly stale read is
        // acceptable but a torn / reordered read is not. Volatile.Write pins ordering and
        // forces a 64-bit aligned single store (the Interlocked.Add below also serves as a
        // release fence for the gauge writes that precede each Add).
        Volatile.Write(ref LastBlockParallelizationPercent, snapshot.ParallelizationPercent);
        Volatile.Write(ref LastBlockTxCount, snapshot.TxCount);
        Interlocked.Add(ref TxCount, snapshot.TxCount);
        Volatile.Write(ref LastBlockReexecutions, snapshot.Reexecutions);
        Interlocked.Add(ref Reexecutions, snapshot.Reexecutions);
        Volatile.Write(ref LastBlockRevalidations, snapshot.Revalidations);
        Interlocked.Add(ref Revalidations, snapshot.Revalidations);
        Volatile.Write(ref LastBlockBlockedReads, snapshot.BlockedReads);
        Interlocked.Add(ref BlockedReads, snapshot.BlockedReads);
    }

    /// <summary>
    /// Resets the cumulative counters to zero so tests asserting on counter deltas don't
    /// bleed into each other. Production never calls this; tests assert on the per-instance
    /// <see cref="BlockStmTransactionsExecutor.LastBlockSnapshot"/> for per-block data.
    /// </summary>
    public static void ResetForTests()
    {
        Interlocked.Exchange(ref TxCount, 0);
        Interlocked.Exchange(ref Reexecutions, 0);
        Interlocked.Exchange(ref Revalidations, 0);
        Interlocked.Exchange(ref BlockedReads, 0);
        LastBlockParallelizationPercent = 0;
        Interlocked.Exchange(ref LastBlockTxCount, 0);
        Interlocked.Exchange(ref LastBlockReexecutions, 0);
        Interlocked.Exchange(ref LastBlockRevalidations, 0);
        Interlocked.Exchange(ref LastBlockBlockedReads, 0);
    }

    /// <summary>
    /// Calculates the parallelization percent for the given block.
    /// </summary>
    /// <param name="txCount">Number of transactions in the block.</param>
    /// <param name="dependentTransactions">Number of transactions with dependencies.</param>
    /// <returns>Parallelization percent in the range 0-100.</returns>
    public static int CalculateParallelizationPercent(int txCount, long dependentTransactions)
    {
        if (txCount <= 0)
        {
            return 100;
        }

        double ratio = (double)(txCount - Math.Min(dependentTransactions, txCount)) / txCount;
        return (int)Math.Round(100.0 * ratio, MidpointRounding.AwayFromZero);
    }
}

/// <summary>
/// Snapshot of parallel block metrics captured after processing.
/// </summary>
/// <param name="TxCount">Number of transactions in the block.</param>
/// <param name="Reexecutions">Number of re-execution attempts.</param>
/// <param name="Revalidations">Number of validation aborts.</param>
/// <param name="BlockedReads">Number of blocked read aborts.</param>
/// <param name="ParallelizationPercent">Percent of transactions executed without re-execution.</param>
public readonly record struct ParallelBlockMetrics(
    int TxCount,
    long Reexecutions,
    long Revalidations,
    long BlockedReads,
    long ParallelizationPercent)
{
    public static ParallelBlockMetrics Empty => new(0, 0, 0, 0, 100);
}
