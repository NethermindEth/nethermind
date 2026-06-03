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

namespace Nethermind.Consensus.Processing.ParallelProcessing.BlockStm;

/// <summary>
/// Coordinates which transaction should be executed and finishing block processing.
/// It also tracks transaction dependencies.
/// </summary>
/// <param name="txCount">Number of transactions in block</param>
/// <param name="parallelTrace">Trace</param>
/// <param name="setPool">Pool of sets</param>
/// <typeparam name="TLogger">Is tracing on</typeparam>
/// <remarks>
/// Algorithm is based on tracking <see cref="_executionIndex"/> and <see cref="_validationIndex"/> of transactions as well as <see cref="_activeTasks"/> that are currently in-flight.
/// Priority is to always schedule the lowest possible transaction that needs work.
/// When transactions finish out-of-order or dependency is detected, then corresponding indexes are decreased and transactions are re-validated or re-executed depending on the need.
/// </remarks>
public class ParallelScheduler<TLogger>(int txCount, ParallelTrace<TLogger> parallelTrace, ObjectPool<HashSet<int>> setPool) where TLogger : struct, IFlag
    // TODO: PooledSet
{
    /// <summary>
    /// Index to fetch the next transaction to execute
    /// </summary>
    private int _executionIndex;

    /// <summary>
    /// Index to fetch the next transaction to validate
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
    private readonly TxState[] _txStates = new TxState[txCount];

    /// <summary>
    /// Maps blocking transaction -> transactions that depend on blocking transaction
    /// </summary>
    private readonly HashSet<int>?[] _txDependencies = new HashSet<int>?[txCount]; // TODO: PooledSet
    private volatile bool _done;

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
        long id = parallelTrace.ReserveId();
        int value = InterlockedEx.Min(ref index, targetValue);

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
            bool done = Math.Min(_executionIndex, _validationIndex) >= txCount // both indexes need to be beyond block size
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
    private TxVersion FetchNext(ref int index, int requiredStatus, int newStatus, [CallerArgumentExpression(nameof(index))] string name = "")
    {
        // if our index is outside the work in a block, we potentially finished the work
        if (Volatile.Read(ref index) >= txCount)
        {
            CheckDone();
            return TxVersion.Empty;
        }

        // we might spawn a new task, optimistically assume so
        Interlocked.Increment(ref _activeTasks);

        // fetch current new index
        int nextTx = Interlocked.Increment(ref index) - 1;
        return TryIncarnate(nextTx, requiredStatus, newStatus, name);
    }

    /// <summary>
    /// Try to create a new task
    /// </summary>
    /// <param name="nextTx">Tx to create a task for</param>
    /// <param name="requiredStatus">Required <see cref="Status"/> for the transaction to succeed</param>
    /// <param name="newStatus">New <see cref="Status"/> for the transaction</param>
    /// <param name="name">Name of the index</param>
    /// <returns>New transaction incarnation, <see cref="TxVersion.Empty"/> if incarnation fails due to wrong <see cref="Status"/> in <see cref="_txStates"/></returns>
    private TxVersion TryIncarnate(int nextTx, int requiredStatus, int newStatus, string name, bool trackActiveTasks = true)
    {
        // if we are in a block size
        if (nextTx < txCount)
        {
            // if we can change the status
            ref TxState state = ref _txStates[nextTx];
            if (Interlocked.CompareExchange(ref state.Status, newStatus, requiredStatus) == requiredStatus)
            {
                // return new incarnation
                if (typeof(TLogger) == typeof(OnFlag) && newStatus != requiredStatus) parallelTrace.Add($"Set Tx {nextTx} status to {TxStatus.GetName(requiredStatus)}");
                return new TxVersion(nextTx, state.Incarnation);
            }
        }

        // if we didn't return a new incarnation, then we didn't spawn a task and need to decrement previous incrementation
        if (trackActiveTasks)
        {
            Interlocked.Decrement(ref _activeTasks);
        }
        return TxVersion.Empty;
    }

    /// <summary>
    /// Tries to get the next task for the calling thread to execute
    /// </summary>
    /// <returns></returns>
    public TxTask NextTask()
    {
        // We want to validate aggressively, so if validation is trailing execution than pick validation
        bool validating = Volatile.Read(ref _validationIndex) < Volatile.Read(ref _executionIndex);
        TxVersion version = validating
            ? FetchNext(ref _validationIndex, TxStatus.Executed, TxStatus.Executed)
            : FetchNext(ref _executionIndex, TxStatus.Ready, TxStatus.Executing);

        return new TxTask(version, validating);
    }

    /// <summary>
    /// Finishes unsuccessful transaction execution and adds dependency between transactions
    /// </summary>
    /// <param name="txIndex">Transaction that is waiting for dependency</param>
    /// <param name="blockingTxIndex">Transaction that is blocking</param>
    /// <param name="fromActiveTask">If abort was called from an active task, not from a static dependency check</param>
    /// <returns>true if dependency was added, false if blocking transaction already finished execution</returns>
    /// <remarks>
    /// After transaction execution in <see cref="ParallelRunner{TLocation, TData, TLogger}.TryExecute"/> either this or <see cref="FinishExecution"/> is called. Both calls end the task.
    /// </remarks>
    public bool AbortExecution(int txIndex, int blockingTxIndex, bool fromActiveTask = true)
    {
        ref TxState blockingTxState = ref _txStates[blockingTxIndex];
        int blockingTxStatus = Volatile.Read(ref blockingTxState.Status);

        // If a blocking transaction is now executed, we shouldn't add dependency, we should just re-execute dependent transaction
        if (blockingTxStatus == TxStatus.Executed)
        {
            if (typeof(TLogger) == typeof(OnFlag)) parallelTrace.Add($"Can't add dependency for tx {txIndex} on {blockingTxIndex}, because it is already executed");
            return false;
        }

        if (typeof(TLogger) == typeof(OnFlag)) parallelTrace.Add($"Adding dependency for tx {txIndex} on {blockingTxIndex}, Tx {blockingTxIndex} status is {TxStatus.GetName(blockingTxStatus)}");
        ref TxState txState = ref _txStates[txIndex];

        // Race window: the blocker can transition to Executed and have its dependency set
        // claimed by FinishExecution while we are mid-Add. To serialize correctly, we hold
        // the dependency-set lock for the entire add+re-check sequence; FinishExecution
        // acquires the same lock when draining. The lock guarantees that either (a) we add
        // before drain and the drain picks us up, or (b) we observe Executed under the lock
        // and abandon the add (returning false → caller re-executes immediately).
        HashSet<int> set = GetDependencySet(blockingTxIndex);
        bool added;
        lock (set)
        {
            // Re-check under the lock: FinishExecution marks Executed before it tries to
            // drain (and acquire this lock), so observing != Executed here means the drain
            // (if any) has not yet started, hence our Add is guaranteed to be visible to
            // whichever thread eventually drains.
            if (Volatile.Read(ref blockingTxState.Status) == TxStatus.Executed)
            {
                added = false;
            }
            else
            {
                Interlocked.Exchange(ref txState.Status, TxStatus.Aborting);
                set.Add(txIndex);
                added = true;
            }
        }

        if (!added)
        {
            // Blocker already executed. Caller will re-execute this tx — we did not park.
            // Do NOT call ResumeDependencies here: that would race FinishExecution's claim
            // and could double-return the set to the pool. The caller handles the false
            // return by scheduling an immediate re-execute.
            if (typeof(TLogger) == typeof(OnFlag)) parallelTrace.Add($"Blocker {blockingTxIndex} already executed; skipping dependency-add for tx {txIndex}");
            return false;
        }

        if (typeof(TLogger) == typeof(OnFlag)) parallelTrace.Add($"Dependency added for tx {txIndex} on {blockingTxIndex} to set {set.GetHashCode()}");

        if (fromActiveTask)
        {
            // This task execution has ended
            Interlocked.Decrement(ref _activeTasks);
        }

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
    /// Atomically claims and drains the dependency set parked on <paramref name="blockingTxIndex"/>,
    /// waking every parked dependent and pushing the execution index back so they re-run.
    /// </summary>
    /// <remarks>
    /// Only the thread that wins the <c>Interlocked.Exchange</c> claim drains and returns the
    /// set to the pool. Concurrent callers see the slot already nulled and exit. This is the
    /// single ownership point for the set's lifecycle — prior to this fix, both
    /// <see cref="AbortExecution"/> (post-add race-detect path) and <see cref="FinishExecution"/>
    /// could see the same set and both return it to the pool, corrupting subsequent borrowers.
    /// AbortExecution no longer calls this method; it short-circuits to "re-execute" on the
    /// blocker-already-Executed race instead.
    /// </remarks>
    private void ResumeDependencies(int blockingTxIndex)
    {
        // Atomic single-owner claim. Only one thread per (blocker, set instance) wins.
        HashSet<int>? dependentTxs = Interlocked.Exchange(ref _txDependencies[blockingTxIndex], null);
        if (dependentTxs is null)
        {
            return;
        }

        int min = int.MaxValue;

        // Lock for memory ordering vs AbortExecution's add — the contract is that any Add()
        // performed under this lock by a still-in-flight Abort is observed here.
        lock (dependentTxs)
        {
            foreach (int tx in dependentTxs)
            {
                min = Math.Min(min, tx);
                SetReady(tx);
            }

            if (typeof(TLogger) == typeof(OnFlag)) parallelTrace.Add($"Resumed dependencies by Tx {blockingTxIndex}: {string.Join(", ", dependentTxs)}");
            dependentTxs.Clear();
        }

        // Return to pool AFTER releasing the lock. Pool entries must be returned exactly once;
        // the Interlocked.Exchange above ensures exactly one ResumeDependencies call observes
        // a non-null set per (blocker, set instance) pair.
        setPool.Return(dependentTxs);

        if (min != int.MaxValue)
        {
            DecreaseIndex(ref _executionIndex, min);
        }
    }

    /// <summary>
    /// Resets transaction state in <see cref="_txStates"/> to <see cref="TxStatus.Ready"/> and
    /// increments its <see cref="TxState.Incarnation"/> in a single atomic step.
    /// </summary>
    /// <remarks>
    /// The previous implementation did <c>Interlocked.Exchange(Status, Ready)</c> followed by a
    /// plain <c>state.Incarnation++</c> — a torn (Status, Incarnation) write. A worker could
    /// observe <c>Ready</c> after the Status publish but before the Incarnation increment is
    /// visible, claim the tx via CAS, and execute under the OLD incarnation. A subsequent
    /// <see cref="TryValidationAbort"/> then fails to match the now-incremented Incarnation in
    /// the live state and the tx becomes silently un-abortable. On ARM/AArch64 (weaker memory
    /// model) this is observable; on x86 TSO masks it. We use the same packed-CAS pattern as
    /// <see cref="TryValidationAbort"/>: write Status+Incarnation as a single 64-bit value.
    /// </remarks>
    /// <param name="txIndex">Transaction index whose state to advance.</param>
    private void SetReady(int txIndex)
    {
        ref TxState state = ref _txStates[txIndex];
        ref long stateInt = ref Unsafe.As<TxState, long>(ref state);

        // Loop in case another thread races (precondition is Aborting, but a CAS loop also
        // tolerates concurrent Status writes from a stale path). Cost on uncontended paths is
        // one CAS — same as Interlocked.Exchange.
        while (true)
        {
            long current = Volatile.Read(ref stateInt);
            TxState currentState = Unsafe.As<long, TxState>(ref current);
            TxState newState = new(TxStatus.Ready, currentState.Incarnation + 1);
            long newInt = Unsafe.As<TxState, long>(ref newState);
            if (Interlocked.CompareExchange(ref stateInt, newInt, current) == current)
            {
                if (typeof(TLogger) == typeof(OnFlag)) parallelTrace.Add($"Set Tx {txIndex} status to Ready, incarnation {newState.Incarnation}");
                return;
            }
        }
    }

    /// <summary>
    /// Finishes the successful transaction execution
    /// </summary>
    /// <param name="version">TxVersion of transaction being executed</param>
    /// <param name="wroteNewLocation">If the transaction wrote any new location, compared to previous incarnations</param>
    /// <returns></returns>
    /// <remarks>
    /// /// After transaction execution in <see cref="ParallelRunner{TLocation, TData, TLogger}.TryExecute"/> either this or <see cref="AbortExecution"/> is called. Both calls end the task.
    /// </remarks>
    public TxTask FinishExecution(TxVersion version, bool wroteNewLocation)
    {
        int txIndex = version.TxIndex;
        ref TxState state = ref _txStates[txIndex];

        // Mark Executed BEFORE claiming dependencies, so any concurrent AbortExecution that
        // started before this point and observes Executed under the dependency-set lock will
        // short-circuit (return false) instead of racing us for the set ownership. The mark
        // is the ordering point: AbortExecution adds to the set only when its in-lock
        // re-check observes != Executed; we observe such adds when we drain below.
        Interlocked.Exchange(ref state.Status, TxStatus.Executed);
        if (typeof(TLogger) == typeof(OnFlag)) parallelTrace.Add($"Set Tx {txIndex} status to Executed");

        ResumeDependencies(txIndex);

        // if the validation index already progressed beyond this transaction
        if (Volatile.Read(ref _validationIndex) > txIndex)
        {
            // if a new location was written, we need to redo subsequent transaction validations
            if (wroteNewLocation)
            {
                DecreaseIndex(ref _validationIndex, txIndex);
            }
            else
            {
                if (typeof(TLogger) == typeof(OnFlag)) parallelTrace.Add($"WorkAvailable.Set from FinishExecution of {version}");
                // validate this transaction
                // don't decrement _activeTasks as we spawn a new one
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
    /// <returns>true if successful, false <see cref="TxState.Status"/> in not <see cref="TxStatus.Aborting"/> or <see cref="TxState.Incarnation"/> changed</returns>
    public bool TryValidationAbort(TxVersion version)
    {
        (int txIndex, int incarnation) = version;
        ref TxState state = ref _txStates[txIndex];
        ref long stateInt = ref Unsafe.As<TxState, long>(ref state);
        TxState value = new(TxStatus.Aborting, incarnation);
        TxState requiredState = new(TxStatus.Executed, incarnation);
        long requiredInt = Unsafe.As<TxState, long>(ref requiredState);
        long valueInt = Unsafe.As<TxState, long>(ref value);

        // hacky way of atomically updating both status and incarnation
        bool abort = Interlocked.CompareExchange(ref stateInt, valueInt, requiredInt) == requiredInt;
        if (typeof(TLogger) == typeof(OnFlag) && abort)
        {
            parallelTrace.Add($"Set Tx {txIndex} status to Aborting");
        }

        return abort;
    }

    /// <summary>
    /// Finishes transaction validation
    /// </summary>
    /// <param name="txIndex">tx index</param>
    /// <param name="aborted">if transaction was aborted</param>
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

            // if execution index already progressed try re-executing the transaction immediately
            if (Volatile.Read(ref _executionIndex) > txIndex)
            {
                TxVersion incarnation = TryIncarnate(txIndex, TxStatus.Ready, TxStatus.Executing, nameof(_executionIndex), trackActiveTasks: false);
                if (!incarnation.IsEmpty)
                {
                    // don't decrement _activeTasks as we spawn a new one
                    return new TxTask(incarnation, false);
                }
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
/// <param name="TxVersion"></param>
/// <param name="Validating"></param>
public readonly record struct TxTask(TxVersion TxVersion, bool Validating)
{
    public static readonly TxTask Empty = new(TxVersion.Empty, false);
    public bool IsEmpty => TxVersion.IsEmpty;
    public override string ToString() => IsEmpty ? "Empty" : $"{(Validating ? "Validating" : "Executing")} {TxVersion}";
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

public sealed class ParallelScheduler(int txCount, ParallelTrace<OffFlag> parallelTrace, ObjectPool<HashSet<int>> setPool)
    : ParallelScheduler<OffFlag>(txCount, parallelTrace, setPool);
