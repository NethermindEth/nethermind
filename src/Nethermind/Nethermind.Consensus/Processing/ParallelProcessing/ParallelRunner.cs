// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core.Collections;
using Nethermind.Core.Threading;

namespace Nethermind.Consensus.Processing.ParallelProcessing;

public class ParallelRunner<TLocation, TLogger>(
    ParallelScheduler<TLogger> scheduler,
    MultiVersionMemory<TLocation, TLogger> memory,
    ParallelTrace<IsTracing> parallelTrace,
    IVm<TLocation> vm,
    int? concurrencyLevel = null) where TLogger : struct, IIsTracing where TLocation : notnull
{
    private int _threadIndex = 0;

    public async Task Run()
    {
        int concurrency = concurrencyLevel ?? Environment.ProcessorCount;
        using ArrayPoolList<Task> tasks = new ArrayPoolList<Task>(concurrency);
        for (int i = 0; i < concurrency; i++)
        {
            tasks.Add(Task.Run(Loop));
        }

        await Task.WhenAll(tasks.AsSpan());
    }

    private void Loop()
    {
        int threadIndex = Interlocked.Increment(ref _threadIndex);
        using var handle = Thread.CurrentThread.BoostPriorityHighest();
        TxTask task = scheduler.NextTask();
        do
        {
            if (typeof(TLogger) == typeof(IsTracing) && !task.IsEmpty) parallelTrace.Add($"NextTask: {task} on thread {threadIndex}");
            task = task switch
            {
                { IsEmpty: true } => scheduler.NextTask(),
                { Validating: false } => TryExecute(task),
                { Validating: true } => NeedsReexecution(task.Version)
            };
        } while (!scheduler.Done);

        if (typeof(TLogger) == typeof(IsTracing)) parallelTrace.Add($"Thread {threadIndex} finished");
    }

    private TxTask TryExecute(TxTask task) =>
        vm.TryExecute(task.Version.TxIndex, out Version? blockingTx, out var readSet, out var writeSet) == Status.ReadError
            ? !scheduler.AddDependency(task.Version.TxIndex, blockingTx!.Value.TxIndex)
                ? task
                : new TxTask(Version.Empty, false)
            : scheduler.FinishExecution(task.Version, memory.Record(task.Version, readSet, writeSet));


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

public interface IVm<TLocation> where TLocation : notnull
{
    public Status TryExecute(ushort txIndex, out Version? blockingTx, out HashSet<Read<TLocation>> readSet, out Dictionary<TLocation, byte[]> writeSet);
}
