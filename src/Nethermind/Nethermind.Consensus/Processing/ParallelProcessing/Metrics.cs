// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.ComponentModel;
using System.Threading;
using Nethermind.Core.Attributes;

namespace Nethermind.Consensus.Processing.ParallelProcessing;

public static class Metrics
{
    private static ParallelBlockMetrics _lastBlock;
    private static long LastBlockSnapshotSequence;

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
        _lastBlock = snapshot;
        Interlocked.Increment(ref LastBlockSnapshotSequence);
        LastBlockParallelizationPercent = snapshot.ParallelizationPercent;
        Interlocked.Add(ref TxCount, LastBlockTxCount = snapshot.TxCount);
        Interlocked.Add(ref Reexecutions, LastBlockReexecutions = snapshot.Reexecutions);
        Interlocked.Add(ref Revalidations, LastBlockRevalidations = snapshot.Revalidations);
        Interlocked.Add(ref BlockedReads, LastBlockBlockedReads = snapshot.BlockedReads);

    }

    /// <summary>
    /// Gets the last block snapshot for the current execution context.
    /// </summary>
    /// <param name="snapshot">The snapshot if available.</param>
    /// <returns>True when a snapshot is available for the current context.</returns>
    public static bool TryGetLastBlockSnapshot(out ParallelBlockMetrics snapshot)
    {
        if (Interlocked.Read(ref LastBlockSnapshotSequence) == 0)
        {
            snapshot = default;
            return false;
        }

        snapshot = _lastBlock;
        return true;
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
