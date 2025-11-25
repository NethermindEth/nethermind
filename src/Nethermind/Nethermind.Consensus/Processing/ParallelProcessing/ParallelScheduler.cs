// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Extensions.ObjectPool;
using Nethermind.Core;
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
/// <remarks>
/// Algorithm is based on tracking <see cref="_executionIndex"/> and <see cref="_validationIndex"/> of transactions as well as <see cref="_activeTasks"/> that are currently in-flight.
/// Priority is to always schedule the lowest possible transaction that needs work.
/// When transactions finish out-of-order or dependency is detected then corresponding indexes are decreased and transactions are re-validated or re-executed depending on the need.
/// </remarks>
public class ParallelScheduler<TLogger>(int blockSize, ParallelTrace<TLogger> parallelTrace, ObjectPool<HashSet<int>> setPool) where TLogger : struct, IFlag
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
    private readonly HashSet<int>?[] _txDependencies = new HashSet<int>?[blockSize];
    private volatile bool _done = false;

    /// <summary>
    /// Indicates all work has been completed
    /// </summary>
    public bool Done => _done;

    /// <summary>
    /// Decreases one of <see cref="_executionIndex"/> or <see cref="_validationIndex"/>
    /// </summary>
    /// <param name="index">Reference to index to decrease</param>
    /// <param name="targetValue">Value to target the index</param>
    /// <param name="name">Name of the index</param>
    private void DecreaseIndex(ref int index, int targetValue, [CallerArgumentExpression(nameof(index))] string name = "")
    {
        // we should keep the index minimal of current and target
        static int Mutator(int current, int target) => Math.Min(current, target);

        long id = parallelTrace.ReserveId();
        int value = InterlockedEx.MutateValue(ref index, targetValue, Mutator);

        if (typeof(TLogger) == typeof(OnFlag)) parallelTrace.Add($"WorkAvailable.Set from DecreaseIndex {name} to {value}");

        // increase the counter of decreases
        int decreaseCount = Interlocked.Increment(ref _decreaseCount);
        if (typeof(TLogger) == typeof(OnFlag)) parallelTrace.Add(id, $"Decreased {name} index to {value}, decrease count: {decreaseCount}");
    }

    /// <summary>
    /// Checks if all work has been done
    /// </summary>
    private void CheckDone()
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
                if (typeof(TLogger) == typeof(OnFlag)) parallelTrace.Add("Done");
            }
        }
    }

    /// <summary>
    /// Fetches next index
    /// </summary>
    /// <param name="index">reference to index being fetched, either <see cref="_executionIndex"/> or <see cref="_validationIndex"/></param>
    /// <param name="requiredStatus"><see cref="Status"/> of transaction in <see cref="_txStates"/> needed for it to be able to be fetched</param>
    /// <param name="newStatus">New <see cref="Status"/> to be set in <see cref="_txStates"/></param>
    /// <param name="name">Name of the intex</param>
    /// <returns></returns>
    private Version FetchNext(ref int index, int requiredStatus, int newStatus, [CallerArgumentExpression(nameof(index))] string name = "")
    {
        // if our index is outside the work in a block we potentially finished the work
        if (Volatile.Read(ref index) >= blockSize)
        {
            CheckDone();
            return Version.Empty;
        }

        // we might spawn a new task, optimistically assume so
        Interlocked.Increment(ref _activeTasks);

        // fetch current new index
        int nextTx = Interlocked.Increment(ref index) - 1;
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
    private Version TryIncarnate(int nextTx, int requiredStatus, int newStatus, string name)
    {
        // if we are in a block size
        if (nextTx < blockSize)
        {
            // if we can change status
            ref TxState state = ref _txStates[nextTx];
            if (Interlocked.CompareExchange(ref state.Status, newStatus, requiredStatus) == requiredStatus)
            {
                // return new incarnation
                if (typeof(TLogger) == typeof(OnFlag) && newStatus != requiredStatus) parallelTrace.Add($"Set Tx {nextTx} status to {TxStatus.GetName(requiredStatus)}");
                return new Version(nextTx, state.Incarnation);
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
    public bool AbortExecution(int txIndex, int blockingTxIndex)
    {
        ref TxState blockingTxState = ref _txStates[blockingTxIndex];
        int blockingTxStatus = Volatile.Read(ref blockingTxState.Status);

        // If blocking transaction is now executed, we shouldn't add dependency, we should just re-execute dependent transaction
        if (blockingTxStatus == TxStatus.Executed)
        {
            if (typeof(TLogger) == typeof(OnFlag)) parallelTrace.Add($"Can't add dependency for tx {txIndex} on {blockingTxIndex}, because it is already executed");
            return false;
        }

        if (typeof(TLogger) == typeof(OnFlag)) parallelTrace.Add($"Adding dependency for tx {txIndex} on {blockingTxIndex}, Tx {blockingTxIndex} status is {TxStatus.GetName(blockingTxStatus)}");
        ref TxState txState = ref _txStates[txIndex];
        Interlocked.Exchange(ref txState.Status, TxStatus.Aborting);
        if (typeof(TLogger) == typeof(OnFlag)) parallelTrace.Add($"Set Tx {txIndex} status to Aborting");

        HashSet<int> set = GetDependencySet(blockingTxIndex);
        lock (set)
        {
            set.Add(txIndex);
        }

        if (typeof(TLogger) == typeof(OnFlag)) parallelTrace.Add($"Dependency added for tx {txIndex} on {blockingTxIndex} to set {set.GetHashCode()}");

        // if blocking transaction finished execution while we were adding the dependency then we need to now call resume dependencies ASAP
        // This missing was one of issues in original paper
        blockingTxStatus = Volatile.Read(ref blockingTxState.Status);
        if (blockingTxStatus == TxStatus.Executed)
        {
            ResumeDependencies(set, blockingTxIndex);
            if (typeof(TLogger) == typeof(OnFlag)) parallelTrace.Add($"Resume dependencies by Tx {blockingTxIndex} while adding for {txIndex} on race condition");
        }
        else
        {
            if (typeof(TLogger) == typeof(OnFlag)) parallelTrace.Add($"Can't resume dependencies by Tx {blockingTxIndex}, because Tx {blockingTxIndex} status is {TxStatus.GetName(blockingTxStatus)}");
        }

        // This task execution has ended
        Interlocked.Decrement(ref _activeTasks);
        return true;

        // Lazy & pooled way of handling dependency sets
        HashSet<int> GetDependencySet(int index)
        {
            HashSet<int> newSet = setPool.Get();
            HashSet<int>? currentSet = Interlocked.CompareExchange(ref _txDependencies[index], newSet, null);
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
    private void ResumeDependencies(HashSet<int>? dependentTxs, int blockingTxIndex)
    {
        // if there are any dependent transactions
        if (dependentTxs?.Count > 0)
        {
            // minimal transaction
            int min = int.MaxValue;

            // needs to be locked!
            lock (dependentTxs)
            {
                // SetReady each transaction
                foreach (int tx in dependentTxs)
                {
                    min = Math.Min(min, tx);
                    SetReady(tx);
                }

                // Clear dependencies, they can be re-added when transactions are executed again
                if (typeof(TLogger) == typeof(OnFlag)) parallelTrace.Add($"Resumed dependencies by Tx {blockingTxIndex}: {string.Join(", ", dependentTxs)}");
                dependentTxs.Clear();
                setPool.Return(dependentTxs);
            }

            // Decrease execution index to the smallest found transaction
            // We need to re-execute them now
            if (min != int.MaxValue)
            {
                DecreaseIndex(ref _executionIndex, min);
            }
        }
    }

    /// <summary>
    /// Resets transaction state in <see cref="_txStates"/> to <see cref="TxStatus.Ready"/> and increase its <see cref="TxState.Incarnation"/>
    /// </summary>
    /// <param name="txIndex"></param>
    private void SetReady(int txIndex)
    {
        ref TxState state = ref _txStates[txIndex];
        Interlocked.Exchange(ref state.Status, TxStatus.Ready);
        if (typeof(TLogger) == typeof(OnFlag)) parallelTrace.Add($"Set Tx {txIndex} status to Ready");
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
        int txIndex = version.TxIndex;
        ref TxState state = ref _txStates[txIndex];
        Interlocked.Exchange(ref state.Status, TxStatus.Executed);
        if (typeof(TLogger) == typeof(OnFlag)) parallelTrace.Add($"Set Tx {txIndex} status to Executed");

        HashSet<int> dependencies = Interlocked.Exchange(ref _txDependencies[txIndex], null);
        ResumeDependencies(dependencies, txIndex);

        // if validation index already progressed beyond this transaction
        if (Volatile.Read(ref _validationIndex) > txIndex)
        {
            // if new location was written, we need to re-do subsequent transaction validations
            if (wroteNewLocation)
            {
                DecreaseIndex(ref _validationIndex, txIndex);
            }
            else
            {
                if (typeof(TLogger) == typeof(OnFlag)) parallelTrace.Add($"WorkAvailable.Set from FinishExecution of {version}");
                // validate this transaction
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
        ref long stateInt = ref Unsafe.As<TxState, long>(ref state);
        TxState value = new TxState(TxStatus.Aborting, version.Incarnation);
        TxState requiredState = new TxState(TxStatus.Executed, version.Incarnation);
        long requiredInt = Unsafe.As<TxState, long>(ref requiredState);
        long valueInt = Unsafe.As<TxState, long>(ref value);

        // hacky way of atomically updating both status and incarnation
        bool abort = Interlocked.CompareExchange(ref stateInt, valueInt, requiredInt) == requiredInt;
        if (typeof(TLogger) == typeof(OnFlag) && abort)
        {
            parallelTrace.Add($"Set Tx {version.TxIndex} status to Aborting");
        }

        return abort;
    }

    /// <summary>
    /// Finishes transaction validation
    /// </summary>
    /// <param name="txIndex">tx index</param>
    /// <param name="aborted">was transaction aborted</param>
    /// <returns>potentially same tx incarnation to execute</returns>
    public TxTask FinishValidation(int txIndex, bool aborted)
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
/// <param name="status"><see cref="TxStatus"/> of transaction</param>
/// <param name="incarnation">Incarnation number of transaction</param>
[StructLayout(LayoutKind.Explicit)]
public struct TxState(int status, int incarnation)
{
    [FieldOffset(0)]
    public int Status = status;

    [FieldOffset(4)]
    public int Incarnation = incarnation;
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
    public const int Ready = 0;

    /// <summary>
    /// Currently executing a task
    /// </summary>
    /// <remarks>
    /// Can be progressed to <see cref="Executed"/> by <see cref="ParallelScheduler{TLogger}.FinishExecution"/>
    /// </remarks>
    public const int Executing = 1;

    /// <summary>
    /// Task already executed
    /// </summary>
    /// <remarks>
    /// Can be progressed to <see cref="Aborting"/> by <see cref="ParallelScheduler{TLogger}.TryValidationAbort"/>
    /// </remarks>
    public const int Executed = 2;

    /// <summary>
    /// Task was aborted, which means dependency was detected and it is blocked by other transaction execution
    /// </summary>
    /// <remarks>
    /// Can be progressed to <see cref="Ready"/> by <see cref="ParallelScheduler{TLogger}.FinishExecution"/> of a blocking transaction (which calls <see cref="ParallelScheduler{TLogger}.ResumeDependencies"/>)
    /// Can be progressed to <see cref="Ready"/> by <see cref="ParallelScheduler{TLogger}.AbortExecution"/> when blocking transaction already executed before dependency added
    /// Can be progressed to <see cref="Ready"/> by <see cref="ParallelScheduler{TLogger}.FinishValidation"/> when aborted and _executionIndex already progressed beyond the validating transaction (blocking tx already re-executing)
    /// </remarks>
    public const int Aborting = 3;

    public static string GetName(int status) =>
        status switch
        {
            Ready => "Ready",
            Executing => "Executing",
            Executed => "Executed",
            Aborting => "Aborting",
            _ => "Unknown"
        };
}
