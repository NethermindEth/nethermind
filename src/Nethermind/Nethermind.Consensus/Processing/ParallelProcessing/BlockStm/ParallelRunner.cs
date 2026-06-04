// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core.Collections;
using Nethermind.Core.Threading;

namespace Nethermind.Consensus.Processing.ParallelProcessing.BlockStm;

/// <summary>
/// Worker pool that drives the Block-STM scheduler to completion. Spawns
/// <paramref name="concurrencyLevel"/> workers; each calls <see cref="ParallelScheduler.NextTask"/>
/// and either executes a tx or validates one until the scheduler is <see cref="ParallelScheduler.Done"/>.
/// </summary>
public sealed class ParallelRunner(
    ParallelScheduler scheduler,
    MultiVersionMemory memory,
    IParallelTransactionProcessor parallelTransactionProcessor,
    ParallelBlockMetricsCollector metrics,
    int? concurrencyLevel = null) : IDisposable
{
    private readonly CancellationTokenSource _cts = new();

    public async Task Run()
    {
        int concurrency = concurrencyLevel ?? Environment.ProcessorCount;
        using ArrayPoolList<Task> tasks = new(concurrency);
        for (int i = 0; i < concurrency; i++)
        {
            tasks.Add(Task.Run(Loop));
        }
        await Task.WhenAll(tasks.AsSpan());
    }

    private void Loop()
    {
        CancellationToken token = _cts.Token;
        try
        {
            using ThreadExtensions.Disposable handle = Thread.CurrentThread.SetHighestPriority();
            TxTask task = scheduler.NextTask();
            do
            {
                // Three task kinds; only one task per transaction is active at a time.
                task = task switch
                {
                    { IsEmpty: true } => scheduler.NextTask(),
                    { Validating: false } => TryExecute(task),
                    { Validating: true } => NeedsReexecution(task.TxVersion)
                };
            } while (!scheduler.Done && !token.IsCancellationRequested);
        }
        catch
        {
            _cts.Cancel();
            throw;
        }
    }

    private TxTask TryExecute(TxTask task)
    {
        metrics.RecordExecutionAttempt(task.TxVersion.TxIndex);
        Status status = parallelTransactionProcessor.TryExecute(task.TxVersion, out int? blockingTx, out bool writeSetChanged);
        if (status == Status.ReadError)
        {
            metrics.RecordBlockedRead(task.TxVersion.TxIndex);
            // If AbortExecution returns false, the blocker finished while we were parking;
            // re-run this task immediately.
            int blocking = blockingTx ?? throw new InvalidOperationException("Blocking transaction index cannot be null");
            return scheduler.AbortExecution(task.TxVersion.TxIndex, blocking) ? TxTask.Empty : task;
        }

        return scheduler.FinishExecution(task.TxVersion, writeSetChanged);
    }

    private TxTask NeedsReexecution(TxVersion version)
    {
        bool aborted = !memory.ValidateReadSet(version.TxIndex) && scheduler.TryValidationAbort(version);
        if (aborted)
        {
            metrics.RecordValidationFailure(version.TxIndex);
            memory.ConvertWritesToEstimates(version.TxIndex);
        }
        return scheduler.FinishValidation(version.TxIndex, aborted);
    }

    public void Dispose() => _cts.Dispose();
}

/// <summary>Executes a single transaction attempt and records its read/write sets.</summary>
public interface IParallelTransactionProcessor
{
    /// <param name="version">Transaction version (index + incarnation) to execute.</param>
    /// <param name="blockingTx">If the result is <see cref="Status.ReadError"/>, the index of the tx whose Estimate was observed.</param>
    /// <param name="writeSetChanged">True when the published write-set may invalidate higher txs' validations.</param>
    Status TryExecute(TxVersion version, out int? blockingTx, out bool writeSetChanged);
}
