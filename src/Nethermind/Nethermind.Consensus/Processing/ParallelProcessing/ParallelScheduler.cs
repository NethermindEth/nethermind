// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using Microsoft.Extensions.ObjectPool;
using Nethermind.Core.Tasks;
using Nethermind.Core.Threading;

namespace Nethermind.Consensus.Processing.ParallelProcessing;

/// <summary>
/// Coordinates which transaction should be executed and finishing block processing.
/// It also tracks transaction dependencies.
/// </summary>
/// <param name="blockSize">Number of transactions in block</param>
/// <param name="parallelTrace">Trace</param>
/// <param name="setPool">Pool of sets</param>
/// <typeparam name="TLogger">Is tracing on</typeparam>
public class ParallelScheduler<TLogger>(ushort blockSize, ParallelTrace<TLogger> parallelTrace, ObjectPool<HashSet<ushort>> setPool) where TLogger : struct, IIsTracing
{
    /// <summary>
    /// Index to fetch next transaction to execute
    /// </summary>
    private int _executionIndex;

    /// <summary>
    /// Index to fetch next transaction to validate
    /// </summary>
    private int _validationIndex;

    /// <summary>
    /// Helper counter to track how many times <see cref="_executionIndex"/> and <see cref="_validationIndex"/> were decreased
    /// </summary>
    private int _decreaseCount;

    /// <summary>
    /// Counter to track how many tasks are still active
    /// </summary>
    private int _activeTasks;

    /// <summary>
    /// Tracks <see cref="TxState"/> for each transaction in block
    /// </summary>
    private readonly TxState[] _txStates = new TxState[blockSize];

    /// <summary>
    /// Maps blocking transaction -> transactions that depend on blocking transaction
    /// </summary>
    private readonly HashSet<ushort>?[] _txDependencies = new HashSet<ushort>?[blockSize];
    private volatile bool _done = false;

    /// <summary>
    /// Indicates all work has been completed
    /// </summary>
    public bool Done => _done;

    /// <summary>
    /// Signals new work became available
    /// </summary>
    public AsyncManualResetEventSlim WorkAvailable { get; } = new(blockSize > 0);

    /// <summary>
    /// Decreases one of <see cref="_executionIndex"/> or <see cref="_validationIndex"/>
    /// </summary>
    /// <param name="index">Reference to index to decrease</param>
    /// <param name="targetValue">Value to target the index</param>
    /// <param name="name">Name of the index</param>
    private void DecreaseIndex(ref int index, int targetValue, [CallerArgumentExpression(nameof(index))] string name = "")
    {
        // We should keep the index minimal of current and target
        static int Mutator(int current, int target) => Math.Min(current, target);

        long id = parallelTrace.ReserveId();
        int value = InterlockedEx.MutateValue(ref index, targetValue, Mutator);

        // If we decreased the index now there is new work to do.
        WorkAvailable.Set();
        if (typeof(TLogger) == typeof(IsTracing)) parallelTrace.Add($"WorkAvailable.Set from DecreaseIndex {name} to {value}");

        // Increase the counter of decreases
        int decreaseCount = Interlocked.Increment(ref _decreaseCount);
        if (typeof(TLogger) == typeof(IsTracing)) parallelTrace.Add(id, $"Decreased {name} index to {value}, decrease count: {decreaseCount}");
    }

    /// <summary>
    /// Checks if all work has been done
    /// </summary>
    private bool CheckDone()
    {
        if (!_done)
        {
            int observedCount = Volatile.Read(ref _decreaseCount);
            bool done = Math.Min(_executionIndex, _validationIndex) >= blockSize // both indexes need to be beyond block size
                        && Volatile.Read(ref _activeTasks) == 0 // and no tasks currently in flight (that could decrease the indexes)
                        && observedCount == Volatile.Read(ref _decreaseCount); // and no decreases happened while we are doing the check
            if (done)
            {
                _done = true;
                // unblock threads to finish them
                WorkAvailable.Set();
                if (typeof(TLogger) == typeof(IsTracing))
                {
                    parallelTrace.Add("Done");
                    parallelTrace.Add("WorkAvailable.Set from CheckDone set to true");
                }

                return true;
            }
        }
        else
        {
            // unblock threads to finish them
            WorkAvailable.Set();
            if (typeof(TLogger) == typeof(IsTracing)) parallelTrace.Add("WorkAvailable.Set from CheckDone already was true");
            return true;
        }

        // WorkAvailable.Reset?
        return false;
    }

    /// <summary>
    /// Fetches next index
    /// </summary>
    /// <param name="index">reference to index being fetched, either <see cref="_executionIndex"/> or <see cref="_validationIndex"/></param>
    /// <param name="requiredStatus"><see cref="Status"/> of transaction in <see cref="_txStates"/> needed for it to be able to be fetched</param>
    /// <param name="newStatus">New <see cref="Status"/> to be set in <see cref="_txStates"/></param>
    /// <param name="name">Name of the intex</param>
    /// <returns></returns>
    private Version FetchNext(ref int index, ushort requiredStatus, ushort newStatus, [CallerArgumentExpression(nameof(index))] string name = "")
    {
        // if our index is outside the work in a block we potentially finished the work
        if (Volatile.Read(ref index) >= blockSize)
        {
            // check if work is done, and return there is no work at the moment
            CheckDone();
            return Version.Empty;
        }

        // We might spawn a new task, optimistically assume so
        Interlocked.Increment(ref _activeTasks);

        // fetch current new index
        ushort nextTx = (ushort)(Interlocked.Increment(ref index) - 1);
        return TryIncarnate(nextTx, requiredStatus, newStatus, name);
    }

    /// <summary>
    /// Try to create new task
    /// </summary>
    /// <param name="nextTx">Tx to create task for</param>
    /// <param name="requiredStatus">Required <see cref="Status"/> for the transaction to succeed</param>
    /// <param name="newStatus">New <see cref="Status"/> for the transaction</param>
    /// <param name="name">Name of the index</param>
    /// <returns>New transaction incarnation, <see cref="Version.Empty"/> if incarnation fails due to wrong <see cref="Status"/> in <see cref="_txStates"/></returns>
    private Version TryIncarnate(ushort nextTx, ushort requiredStatus, ushort newStatus, string name)
    {
        // if we are in a block size
        if (nextTx < blockSize)
        {
            // if we can change status
            ref TxState state = ref _txStates[nextTx];
            if (Interlocked.CompareExchange(ref state.Status, newStatus, requiredStatus) == requiredStatus)
            {
                // return new incarnation
                if (typeof(TLogger) == typeof(IsTracing)) parallelTrace.Add($"Set Tx {nextTx} status to {TxStatus.GetName(requiredStatus)}");
                return new Version(nextTx, state.Incarnation);
            }
        }
        else
        {
            // if we are not done
            if (!_done)
            {
                // we probably don't have anything to do, lets pause the threads until new work can be scheduled
                WorkAvailable.Reset();
                if (typeof(TLogger) == typeof(IsTracing)) parallelTrace.Add($"WorkAvailable.Reset from FetchNext {name} to {nextTx + 1}");
            }
        }

        // if we didn't return new incarnation, then we didn't spawn a task and need to decrement previous incrementation
        Interlocked.Decrement(ref _activeTasks);
        return Version.Empty;
    }

    /// <summary>
    /// Tries to get next task for the calling thread to execute
    /// </summary>
    /// <returns></returns>
    public TxTask NextTask()
    {
        // We want to validate aggressively so if validation is trailing execution than pick validation
        bool validating = Volatile.Read(ref _validationIndex) < Volatile.Read(ref _executionIndex);
        Version version = validating
            ? FetchNext(ref _validationIndex, TxStatus.Executed, TxStatus.Executed)
            : FetchNext(ref _executionIndex, TxStatus.Ready, TxStatus.Executing);

        return new TxTask(version, validating);
    }

    /// <summary>
    /// Finishes unsuccessful transaction execution and adds dependency between transactions
    /// </summary>
    /// <param name="txIndex">Transaction that is waiting for dependency</param>
    /// <param name="blockingTxIndex">Transaction that is blocking</param>
    /// <returns>true if dependency was added, false if blocking transaction already finished execution</returns>
    /// <remarks>
    /// After transaction execution in <see cref="ParallelRunner{TLocation,TLogger}.TryExecute"/> either this or <see cref="FinishExecution"/> is called. Both calls end the task.
    /// </remarks>
    public bool AbortExecution(ushort txIndex, ushort blockingTxIndex)
    {
        ref TxState blockingTxState = ref _txStates[blockingTxIndex];
        ushort blockingTxStatus = Volatile.Read(ref blockingTxState.Status);

        // If blocking transaction is now executed, we shouldn't add dependency, we should just re-execute dependent transaction
        if (blockingTxStatus == TxStatus.Executed)
        {
            if (typeof(TLogger) == typeof(IsTracing)) parallelTrace.Add($"Can't add dependency for tx {txIndex} on {blockingTxIndex}, because it is already executed");
            return false;
        }

        if (typeof(TLogger) == typeof(IsTracing)) parallelTrace.Add($"Adding dependency for tx {txIndex} on {blockingTxIndex}, Tx {blockingTxIndex} status is {TxStatus.GetName(blockingTxStatus)}");
        ref TxState txState = ref _txStates[txIndex];
        Interlocked.Exchange(ref txState.Status, TxStatus.Aborting);
        if (typeof(TLogger) == typeof(IsTracing)) parallelTrace.Add($"Set Tx {txIndex} status to Aborting");

        HashSet<ushort> set = GetDependencySet(blockingTxIndex);
        lock (set)
        {
            set.Add(txIndex);
        }

        if (typeof(TLogger) == typeof(IsTracing)) parallelTrace.Add($"Dependency added for tx {txIndex} on {blockingTxIndex} to set {set.GetHashCode()}");

        // if blocking transaction finished execution while we were adding the dependency then we need to now call resume dependencies ASAP
        // This missing was one of issues in original paper
        blockingTxStatus = Volatile.Read(ref blockingTxState.Status);
        if (blockingTxStatus == TxStatus.Executed)
        {
            ResumeDependencies(set, blockingTxIndex);
            if (typeof(TLogger) == typeof(IsTracing)) parallelTrace.Add($"Resume dependencies by Tx {blockingTxIndex} while adding for {txIndex} on race condition");
        }
        else
        {
            if (typeof(TLogger) == typeof(IsTracing)) parallelTrace.Add($"Can't resume dependencies by Tx {blockingTxIndex}, because Tx {blockingTxIndex} status is {TxStatus.GetName(blockingTxStatus)}");
        }

        // This task execution has ended
        Interlocked.Decrement(ref _activeTasks);
        return true;

        // Lazy & pooled way of handling dependency sets
        HashSet<ushort> GetDependencySet(ushort index)
        {
            HashSet<ushort> newSet = setPool.Get();
            HashSet<ushort>? currentSet = Interlocked.CompareExchange(ref _txDependencies[index], newSet, null);
            if (currentSet is not null)
            {
                setPool.Return(newSet);
                return currentSet;
            }

            return newSet;
        }
    }

    /// <summary>
    /// Resumes dependencies
    /// </summary>
    /// <param name="dependentTxs">Dependent transactions</param>
    /// <param name="blockingTxIndex">Blocking transaction index</param>
    private void ResumeDependencies(HashSet<ushort>? dependentTxs, ushort blockingTxIndex)
    {
        // if there are any dependent transactions
        if (dependentTxs?.Count > 0)
        {
            // minimal transaction
            ushort min = ushort.MaxValue;

            // needs to be locked!
            lock (dependentTxs)
            {
                // SetReady each transaction
                foreach (ushort tx in dependentTxs)
                {
                    min = Math.Min(min, tx);
                    SetReady(tx);
                }

                // Clear dependencies, they can be re-added when transactions are executed again
                if (typeof(TLogger) == typeof(IsTracing)) parallelTrace.Add($"Resumed dependencies by Tx {blockingTxIndex}: {string.Join(", ", dependentTxs)}");
                dependentTxs.Clear();
                setPool.Return(dependentTxs);
            }

            // Decrease execution index to the smallest found transaction
            // We need to re-execute them now
            if (min != ushort.MaxValue)
            {
                DecreaseIndex(ref _executionIndex, min);
            }
        }
    }

    /// <summary>
    /// Resets transaction state in <see cref="_txStates"/> to <see cref="TxStatus.Ready"/> and increase its <see cref="TxState.Incarnation"/>
    /// </summary>
    /// <param name="txIndex"></param>
    private void SetReady(ushort txIndex)
    {
        ref TxState state = ref _txStates[txIndex];
        Interlocked.Exchange(ref state.Status, TxStatus.Ready);
        if (typeof(TLogger) == typeof(IsTracing)) parallelTrace.Add($"Set Tx {txIndex} status to Ready");
        state.Incarnation++;
    }

    /// <summary>
    /// Finishes the successful transaction execution
    /// </summary>
    /// <param name="version">Version of transaction being executed</param>
    /// <param name="wroteNewLocation">If any new location was written by the transaction, compared to previous incarnations</param>
    /// <returns></returns>
    /// <remarks>
    /// /// After transaction execution in <see cref="ParallelRunner{TLocation,TLogger}.TryExecute"/> either this or <see cref="AbortExecution"/> is called. Both calls end the task.
    /// </remarks>
    public TxTask FinishExecution(Version version, bool wroteNewLocation)
    {
        ushort txIndex = version.TxIndex;
        ref TxState state = ref _txStates[txIndex];
        Interlocked.Exchange(ref state.Status, TxStatus.Executed);
        if (typeof(TLogger) == typeof(IsTracing)) parallelTrace.Add($"Set Tx {txIndex} status to Executed");

        HashSet<ushort> dependencies = Interlocked.Exchange(ref _txDependencies[txIndex], null);
        ResumeDependencies(dependencies, txIndex);

        // if validation index already progressed beyond this transaction
        if (Volatile.Read(ref _validationIndex) > txIndex)
        {
            // if new location was written, we need to re-do subsequent transaction validations
            if (wroteNewLocation)
            {
                WorkAvailable.Set(); // TODO: not needed?
                DecreaseIndex(ref _validationIndex, txIndex);
            }
            else
            {
                // validate this transaction
                WorkAvailable.Set(); //TODO: not needed?
                if (typeof(TLogger) == typeof(IsTracing)) parallelTrace.Add($"WorkAvailable.Set from FinishExecution of {version}");
                // don't decrement _activeTasks as we spawn new one
                return new TxTask(version, true);
            }
        }

        // This task execution has ended
        Interlocked.Decrement(ref _activeTasks);
        return TxTask.Empty;
    }

    /// <summary>
    /// Tries to abort the transaction
    /// </summary>
    /// <param name="version">Transaction incarnation</param>
    /// <returns>true if successfull, false <see cref="TxState.Status"/> in not <see cref="TxStatus.Aborting"/> or <see cref="TxState.Incarnation"/> changed</returns>
    public bool TryValidationAbort(Version version)
    {
        ref TxState state = ref _txStates[version.TxIndex];
        ref int stateInt = ref Unsafe.As<TxState, int>(ref state);
        TxState value = new TxState(TxStatus.Aborting, version.Incarnation);
        TxState requiredState = new TxState(TxStatus.Executed, version.Incarnation);
        int requiredInt = Unsafe.As<TxState, int>(ref requiredState);
        int valueInt = Unsafe.As<TxState, int>(ref value);

        // hacky way of atomically updating both status and incarnation
        // TODO: Is explicit TxState struct layout required?
        return Interlocked.CompareExchange(ref stateInt, valueInt, requiredInt) == requiredInt;
    }

    /// <summary>
    /// Finishes transaction validation
    /// </summary>
    /// <param name="txIndex">tx index</param>
    /// <param name="aborted">was transaction aborted</param>
    /// <returns>potentially same tx incarnation to execute</returns>
    public TxTask FinishValidation(ushort txIndex, bool aborted)
    {
        // if aborted
        if (aborted)
        {
            // mark transaction for re-execution
            SetReady(txIndex);

            // re-validate subsequent transactions
            DecreaseIndex(ref _validationIndex, txIndex + 1);

            // if execution index already progressed try re-executing transaction immediately
            if (Volatile.Read(ref _executionIndex) > txIndex)
            {
                // don't decrement _activeTasks as we spawn new one
                return new TxTask(TryIncarnate(txIndex, TxStatus.Ready, TxStatus.Executing, nameof(_executionIndex)), false);
            }
        }

        // This task validation has ended
        Interlocked.Decrement(ref _activeTasks);
        CheckDone();
        return TxTask.Empty;
    }
}

/// <summary>
/// Tx task to execute
/// </summary>
/// <param name="Version"></param>
/// <param name="Validating"></param>
public readonly record struct TxTask(Version Version, bool Validating)
{
    public static readonly TxTask Empty = new(Version.Empty, false);
    public bool IsEmpty => Version.IsEmpty;
    public override string ToString() => IsEmpty ? "Empty" : $"{(Validating ? "Validating" : "Executing")} {Version}";
}

/// <summary>
/// State of each transaction
/// </summary>
/// <param name="Status"><see cref="TxStatus"/> of transaction</param>
/// <param name="Incarnation">Incarnation number of transaction</param>
public record struct TxState(ushort Status, ushort Incarnation)
{
    public ushort Status = Status;
    public ushort Incarnation = Incarnation;
}

/// <summary>
/// Statuses transaction can be in
/// </summary>
public static class TxStatus
{
    /// <summary>
    /// Ready to execute
    /// </summary>
    /// <remarks>
    /// Can be progressed to <see cref="Executing"/> by <see cref="ParallelScheduler{TLogger}.FetchNext"/>
    /// </remarks>
    public const ushort Ready = 0;

    /// <summary>
    /// Currently executing a task
    /// </summary>
    /// <remarks>
    /// Can be progressed to <see cref="Executed"/> by <see cref="ParallelScheduler{TLogger}.FinishExecution"/>
    /// </remarks>
    public const ushort Executing = 1;

    /// <summary>
    /// Task already executed
    /// </summary>
    /// <remarks>
    /// Can be progressed to <see cref="Aborting"/> by <see cref="ParallelScheduler{TLogger}.TryValidationAbort"/>
    /// </remarks>
    public const ushort Executed = 2;

    /// <summary>
    /// Task was aborted, which means dependency was detected and it is blocked by other transaction execution
    /// </summary>
    /// <remarks>
    /// Can be progressed to <see cref="Ready"/> by <see cref="ParallelScheduler{TLogger}.FinishExecution"/> of a blocking transaction (which calls <see cref="ParallelScheduler{TLogger}.ResumeDependencies"/>)
    /// Can be progressed to <see cref="Ready"/> by <see cref="ParallelScheduler{TLogger}.AbortExecution"/> when blocking transaction already executed before dependency added
    /// Can be progressed to <see cref="Ready"/> by <see cref="ParallelScheduler{TLogger}.FinishValidation"/> when aborted and _executionIndex already progressed beyond the validating transaction (blocking tx already re-executing)
    /// </remarks>
    public const ushort Aborting = 3;

    public static string GetName(ushort status) =>
        status switch
        {
            Ready => "Ready",
            Executing => "Executing",
            Executed => "Executed",
            Aborting => "Aborting",
            _ => "Unknown"
        };
}
