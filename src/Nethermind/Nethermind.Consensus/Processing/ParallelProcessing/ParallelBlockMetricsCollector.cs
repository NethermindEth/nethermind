// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;

namespace Nethermind.Consensus.Processing.ParallelProcessing;

/// <summary>
/// Captures per-transaction execution events to build actual parallel block metrics.
/// </summary>
public sealed class ParallelBlockMetricsCollector
{
    private readonly int[] _executionAttempts;
    private readonly int[] _blockedReads;
    private readonly int[] _validationFailures;

    /// <summary>
    /// Initializes a new instance of the <see cref="ParallelBlockMetricsCollector"/> class.
    /// </summary>
    /// <param name="txCount">Number of transactions in the block.</param>
    public ParallelBlockMetricsCollector(int txCount)
    {
        _executionAttempts = new int[txCount];
        _blockedReads = new int[txCount];
        _validationFailures = new int[txCount];
    }

    /// <summary>
    /// Records a transaction execution attempt.
    /// </summary>
    /// <param name="txIndex">Transaction index.</param>
    public void RecordExecutionAttempt(int txIndex) => Interlocked.Increment(ref _executionAttempts[txIndex]);

    /// <summary>
    /// Records that a transaction encountered a blocked read.
    /// </summary>
    /// <param name="txIndex">Transaction index.</param>
    public void RecordBlockedRead(int txIndex) => Interlocked.Increment(ref _blockedReads[txIndex]);

    /// <summary>
    /// Records that a transaction validation failed.
    /// </summary>
    /// <param name="txIndex">Transaction index.</param>
    public void RecordValidationFailure(int txIndex) => Interlocked.Increment(ref _validationFailures[txIndex]);

    /// <summary>
    /// Builds a snapshot of actual parallel block metrics.
    /// </summary>
    /// <returns>Snapshot of parallel block metrics.</returns>
    public ParallelBlockMetrics Snapshot()
    {
        int txCount = _executionAttempts.Length;
        long reexecutions = 0;
        long revalidations = 0;
        long blockedReads = 0;

        for (int i = 0; i < txCount; i++)
        {
            reexecutions += _executionAttempts[i];
            revalidations += _validationFailures[i];
            blockedReads += _blockedReads[i];
        }

        reexecutions -= txCount;

        long parallelizationPercent = Metrics.CalculateParallelizationPercent(txCount, reexecutions);
        return new ParallelBlockMetrics(txCount, reexecutions, revalidations, blockedReads, parallelizationPercent);
    }
}
