// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading.Tasks;

namespace Nethermind.Consensus.Processing.ParallelProcessing;

public class ParallelRunner<TLogger>(
    ParallelScheduler<TLogger> scheduler,
    MultiVersionMemory<TLogger> memory,
    IVm vm,
    ParallelTrace<TLogger> parallelTrace) where TLogger : struct, IIsTracing
{
    public async Task Run()
    {
        TxTask task = scheduler.NextTask();
        do
        {
            TxTask current = task;
            _ = Task.Run(() => RunTask(current));
            task = scheduler.NextTask();
            if (task.IsEmpty) await Task.Delay(10);
        } while (!scheduler.Done);
    }

    private void RunTask(TxTask task)
    {
        while (!task.IsEmpty)
        {
            task = task.Validating ? NeedsReexecution(task.Version) : TryExecute(task.Version);
            if (typeof(TLogger) == typeof(IsTracing) && !task.IsEmpty) parallelTrace.Add($"NextTask (immediate): {task}");
        }
    }

    private TxTask TryExecute(Version version) =>
        vm.TryExecute(version.TxIndex, out Version? blockingTx, out var readSet, out var writeSet) == Status.ReadError
            ? !scheduler.AddDependency(version.TxIndex, blockingTx!.Value.TxIndex)
                ? TryExecute(version)
                : new TxTask(Version.Empty, false)
            : scheduler.FinishExecution(version, memory.Record(version, readSet, writeSet));


    private TxTask NeedsReexecution(Version version)
    {
        bool aborted = !memory.ValidateReadSet(version.TxIndex);
        if (aborted && scheduler.TryValidationAbort(version))
        {
            memory.ConvertWritesToEstimates(version.TxIndex);
        }

        return scheduler.FinishValidation(version.TxIndex, aborted);
    }
}

public interface IVm
{
    public Status TryExecute(ushort txIndex, out Version? blockingTx, out HashSet<Read> readSet, out Dictionary<int, byte[]> writeSet);
}
