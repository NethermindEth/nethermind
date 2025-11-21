// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core.Collections;
using Nethermind.Core.Threading;

namespace Nethermind.Consensus.Processing.ParallelProcessing;

/// <summary>
/// This class is main entry point to Block-STM algorithm, directly responsible for scheduling .net multithreaded code
/// and processing transactions.
/// </summary>
public class ParallelRunner<TLocation, TData, TLogger>(
    ParallelScheduler<TLogger> scheduler,
    MultiVersionMemory<TLocation, TData, TLogger> memory,
    ParallelTrace<TLogger> parallelTrace,
    IVm<TLocation, TData> vm,
    int? concurrencyLevel = null) where TLogger : struct, IIsTracing where TLocation : notnull
{
    private int _threadIndex = -1;

    /// <summary>
    /// Runs the Block-STM based processing
    /// </summary>
    public async Task Run()
    {
        // I previously tried single thread calling scheduler.NextTask()
        // and only running fire & forget Task.Run with TryExecute and NeedsReexecution.
        // But I think this approach ended with less parallelization
        // TODO: revisit when integrated with block processing

        int concurrency = concurrencyLevel ?? Environment.ProcessorCount;
        using ArrayPoolList<Task> tasks = new ArrayPoolList<Task>(concurrency);
        for (int i = 0; i < concurrency; i++)
        {

            tasks.Add(Task.Run(Loop));
        }

        // We need to wait only for first task, if one reads scheduler.Done, all other will too
        await Task.WhenAny(tasks.AsSpan());

        // This seems to perform slightly better without async:
        // ParallelUnbalancedWork.For(0, concurrency, Loop);
    }

    private void Loop()
    {
        Loop(Interlocked.Increment(ref _threadIndex));
    }

    private void Loop(int threadIndex)
    {
        long start = Stopwatch.GetTimestamp();
        using ThreadExtensions.Disposable handle = Thread.CurrentThread.SetHighestPriority();
        TxTask task = scheduler.NextTask();
        do
        {
            if (typeof(TLogger) == typeof(IsTracing) && !task.IsEmpty) parallelTrace.Add($"NextTask: {task} on thread {threadIndex}");
            // There can be 3 kinds of tasks
            // Only 1 task per transaction should be run at the same time
            task = task switch
            {
                { IsEmpty: true } => scheduler.NextTask(), // no-op task - try fetch next
                { Validating: false } => TryExecute(task), // execution task
                { Validating: true } => NeedsReexecution(task.Version) // validation task
            };
        } while (!scheduler.Done);

        if (typeof(TLogger) == typeof(IsTracing)) parallelTrace.Add($"Thread {threadIndex} finished in {Stopwatch.GetElapsedTime(start)}");
    }

    /// <summary>
    /// Executes a transaction
    /// </summary>
    /// <remarks>
    /// If transaction execution ends with <see cref="Status.ReadError"/> then it is blocked by another transaction.
    /// Then we try to add dependency between transactions.
    /// This can fail if blocking tx just finished execution before we managed to add dependency.
    /// In that case we return the task itself for re-execution.
    ///
    /// If dependency was added, we return empty task, for the main loop to fetch next one.
    ///
    /// If execution succeeds, we record read and write sets of it in <see cref="MultiVersionMemory{TLocation,TLogger}"/>
    /// and inform <see cref="ParallelScheduler{TLogger}"/> that it finished.
    /// Scheduler may return a validation task for this transaction as it will be next high-priority.
    /// </remarks>
    private TxTask TryExecute(TxTask task) =>
        vm.TryExecute(task.Version.TxIndex, out Version? blockingTx, out HashSet<Read<TLocation>> readSet, out Dictionary<TLocation, TData> writeSet) == Status.ReadError
            ? !scheduler.AbortExecution(task.Version.TxIndex, blockingTx!.Value.TxIndex)
                ? task
                : TxTask.Empty
            : scheduler.FinishExecution(task.Version, memory.Record(task.Version, readSet, writeSet));


    /// <summary>
    /// Checks if transaction need to be re-executed.
    /// </summary>
    /// <remarks>
    /// First the <see cref="MultiVersionMemory{TLocation,TLogger}.ValidateReadSet"/> is called to check if any of transaction reads are still dependent of pending writes.
    /// If that is the case it uses <see cref="ParallelScheduler{TLogger}.TryValidationAbort"/> to abort the execution and marks all the transaction writes as estimates.
    ///
    /// After that is calls <see cref="ParallelScheduler{TLogger}.FinishValidation"/> to progress the work. This potentially can return a transaction task to execute.
    /// </remarks>
    private TxTask NeedsReexecution(Version version)
    {
        bool aborted = !memory.ValidateReadSet(version.TxIndex) && scheduler.TryValidationAbort(version);
        if (aborted)
        {
            memory.ConvertWritesToEstimates(version.TxIndex);
        }

        return scheduler.FinishValidation(version.TxIndex, aborted);
    }
}

/// <summary>
/// Abstraction of transaction execution.
/// </summary>
public interface IVm<TLocation, TData> where TLocation : notnull
{
    /// <summary>
    /// Execute transaction
    /// </summary>
    /// <param name="txIndex">Transaction index</param>
    /// <param name="blockingTx">Information about transaction this one depends on as it is expected to write to a location this one reads</param>
    /// <param name="readSet">All locations read by the transaction</param>
    /// <param name="writeSet">All locations and values written by the transaction</param>
    /// <returns><see cref="Status.Ok"/> if no dependency detected, <see cref="Status.ReadError"/> if transaction is blocked by other</returns>
    public Status TryExecute(int txIndex, out Version? blockingTx, out HashSet<Read<TLocation>> readSet, out Dictionary<TLocation, TData> writeSet);
}
