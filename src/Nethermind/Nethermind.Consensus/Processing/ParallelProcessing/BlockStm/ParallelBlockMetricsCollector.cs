// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;

namespace Nethermind.Consensus.Processing.ParallelProcessing.BlockStm;

/// <summary>
/// Captures per-transaction execution events to build actual parallel block metrics.
/// </summary>
public sealed class ParallelBlockMetricsCollector(int txCount)
{
    private readonly int[] _executionAttempts = new int[txCount];
    private readonly int[] _blockedReads = new int[txCount];
    private readonly int[] _validationFailures = new int[txCount];

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
        int dependentTxs = 0;
        int maxIncarnations = txCount > 0 ? 1 : 0;

        for (int i = 0; i < txCount; i++)
        {
            int attempts = _executionAttempts[i];
            reexecutions += attempts;
            revalidations += _validationFailures[i];
            blockedReads += _blockedReads[i];

            // "Dependent" = needed at least one re-execution. Counting unique txs (not events)
            // is what gives parallelizationPercent its honest meaning — one badly-contended tx
            // re-executing 20 times shouldn't shrink the reported parallelism the way summing
            // raw events would.
            if (attempts > 1) dependentTxs++;
            if (attempts > maxIncarnations) maxIncarnations = attempts;
        }

        reexecutions -= txCount;

        long parallelizationPercent = Metrics.CalculateParallelizationPercent(txCount, dependentTxs);
        return new ParallelBlockMetrics(txCount, reexecutions, revalidations, blockedReads, parallelizationPercent, maxIncarnations);
    }
}
