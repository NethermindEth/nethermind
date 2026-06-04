// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Extensions.ObjectPool;
using Nethermind.Core.Threading;

namespace Nethermind.Consensus.Processing.ParallelProcessing.BlockStm;

/// <summary>
/// Block-STM scheduler. Decides which transaction a worker should execute or validate next,
/// tracks dependencies between transactions, and decides when the block is done.
/// </summary>
/// <remarks>
/// The algorithm tracks <c>_executionIndex</c>, <c>_validationIndex</c>, and
/// <c>_activeTasks</c>; priority is to always schedule the lowest indexed transaction that
/// needs work. When transactions finish out-of-order or a dependency is detected, the
/// indexes are decreased and transactions are re-validated or re-executed.
/// </remarks>
public sealed class ParallelScheduler(int txCount, ObjectPool<HashSet<int>> setPool)
{
    private int _executionIndex;
    private int _validationIndex;
    private int _decreaseCount;
    private int _activeTasks;

    private readonly TxState[] _txStates = new TxState[txCount];
    private readonly HashSet<int>?[] _txDependencies = new HashSet<int>?[txCount];
    private volatile bool _done;

    public bool Done => _done;

    private void DecreaseIndex(ref int index, int targetValue)
    {
        InterlockedEx.Min(ref index, targetValue);
        Interlocked.Increment(ref _decreaseCount);
    }

    private void CheckDone()
    {
        if (_done) return;

        int observedCount = Volatile.Read(ref _decreaseCount);
        bool done = Math.Min(_executionIndex, _validationIndex) >= txCount
                    && Volatile.Read(ref _activeTasks) == 0
                    && observedCount == Volatile.Read(ref _decreaseCount);
        if (done) _done = true;
    }

    private TxVersion FetchNext(ref int index, int requiredStatus, int newStatus)
    {
        if (Volatile.Read(ref index) >= txCount)
        {
            CheckDone();
            return TxVersion.Empty;
        }

        // Optimistically reserve a task slot — TryIncarnate will release if the CAS fails.
        Interlocked.Increment(ref _activeTasks);
        int nextTx = Interlocked.Increment(ref index) - 1;
        return TryIncarnate(nextTx, requiredStatus, newStatus);
    }

    private TxVersion TryIncarnate(int nextTx, int requiredStatus, int newStatus, bool trackActiveTasks = true)
    {
        if (nextTx < txCount)
        {
            ref TxState state = ref _txStates[nextTx];
            if (Interlocked.CompareExchange(ref state.Status, newStatus, requiredStatus) == requiredStatus)
            {
                return new TxVersion(nextTx, state.Incarnation);
            }
        }

        if (trackActiveTasks)
        {
            Interlocked.Decrement(ref _activeTasks);
        }
        return TxVersion.Empty;
    }

    /// <summary>
    /// Returns the next task for the calling worker — validation when behind execution,
    /// otherwise execution.
    /// </summary>
    public TxTask NextTask()
    {
        // Validate aggressively: if validation lags execution, pick validation first.
        bool validating = Volatile.Read(ref _validationIndex) < Volatile.Read(ref _executionIndex);
        TxVersion version = validating
            ? FetchNext(ref _validationIndex, TxStatus.Executed, TxStatus.Executed)
            : FetchNext(ref _executionIndex, TxStatus.Ready, TxStatus.Executing);

        return new TxTask(version, validating);
    }

    /// <summary>
    /// Park <paramref name="txIndex"/> on the dependency set of <paramref name="blockingTxIndex"/>.
    /// </summary>
    /// <returns>
    /// <c>true</c> if the dependency was registered; <c>false</c> if the blocking tx is already
    /// <see cref="TxStatus.Executed"/> (caller should re-execute immediately).
    /// </returns>
    public bool AbortExecution(int txIndex, int blockingTxIndex, bool fromActiveTask = true)
    {
        ref TxState blockingTxState = ref _txStates[blockingTxIndex];
        if (Volatile.Read(ref blockingTxState.Status) == TxStatus.Executed)
        {
            return false;
        }

        ref TxState txState = ref _txStates[txIndex];

        // Hold the dep-set lock across the add + re-check so FinishExecution's drain either
        // observes our add or we observe Executed and abandon. ResumeDependencies must not
        // be called here — it would race the drain and could double-return the set.
        HashSet<int> set = GetDependencySet(blockingTxIndex);
        bool added;
        lock (set)
        {
            // Re-confirm the slot still names this set — defensive against the dep-set
            // ownership transition (FinishExecution could have Interlocked.Exchange'd the
            // slot to null between GetDependencySet and the lock). If it did, the blocker
            // is past Executed already; treat as already-done.
            if (Volatile.Read(ref _txDependencies[blockingTxIndex]) != set)
            {
                added = false;
            }
            else if (Volatile.Read(ref blockingTxState.Status) == TxStatus.Executed)
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
            return false;
        }

        if (fromActiveTask)
        {
            Interlocked.Decrement(ref _activeTasks);
        }
        return true;

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
    /// Only the thread that wins the <c>Interlocked.Exchange</c> claim drains and returns
    /// the set to the pool — single ownership prevents the prior double-return race.
    /// </remarks>
    private void ResumeDependencies(int blockingTxIndex)
    {
        HashSet<int>? dependentTxs = Interlocked.Exchange(ref _txDependencies[blockingTxIndex], null);
        if (dependentTxs is null) return;

        int min = int.MaxValue;

        // Lock provides memory ordering vs an in-flight AbortExecution still inside its lock.
        lock (dependentTxs)
        {
            foreach (int tx in dependentTxs)
            {
                min = Math.Min(min, tx);
                SetReady(tx);
            }
            dependentTxs.Clear();
        }
        setPool.Return(dependentTxs);

        if (min != int.MaxValue)
        {
            DecreaseIndex(ref _executionIndex, min);
        }
    }

    /// <summary>
    /// Atomically sets Status=Ready and increments Incarnation in one packed CAS — same
    /// pattern as <see cref="TryValidationAbort"/>. A plain Status-write + Incarnation++
    /// is observably torn on weak memory models (ARM): a worker can see Ready before the
    /// bump publishes, execute at the old incarnation, and become un-abortable.
    /// </summary>
    private void SetReady(int txIndex)
    {
        ref TxState state = ref _txStates[txIndex];
        ref long stateInt = ref Unsafe.As<TxState, long>(ref state);

        while (true)
        {
            long current = Volatile.Read(ref stateInt);
            TxState currentState = Unsafe.As<long, TxState>(ref current);
            TxState newState = new(TxStatus.Ready, currentState.Incarnation + 1);
            long newInt = Unsafe.As<TxState, long>(ref newState);
            if (Interlocked.CompareExchange(ref stateInt, newInt, current) == current)
            {
                return;
            }
        }
    }

    /// <summary>Records a successful execution and wakes parked dependents.</summary>
    /// <param name="version">Tx version that just executed.</param>
    /// <param name="writeSetChanged">Whether the published write-set may invalidate higher txs' validations.</param>
    public TxTask FinishExecution(TxVersion version, bool writeSetChanged)
    {
        int txIndex = version.TxIndex;
        ref TxState state = ref _txStates[txIndex];

        // Mark Executed BEFORE claiming dependencies — AbortExecution's in-lock re-check
        // then short-circuits instead of racing us for set ownership.
        Interlocked.Exchange(ref state.Status, TxStatus.Executed);
        ResumeDependencies(txIndex);

        if (Volatile.Read(ref _validationIndex) > txIndex)
        {
            if (writeSetChanged)
            {
                DecreaseIndex(ref _validationIndex, txIndex);
            }
            else
            {
                // Self-validate immediately; don't decrement _activeTasks (we spawn a new task).
                return new TxTask(version, true);
            }
        }

        Interlocked.Decrement(ref _activeTasks);
        return TxTask.Empty;
    }

    /// <summary>
    /// Atomically transitions Executed -> Aborting iff the live Incarnation still matches
    /// <paramref name="version"/>'s. Returns false if the tx has already advanced (a newer
    /// incarnation is in flight) so we don't abort the wrong incarnation.
    /// </summary>
    public bool TryValidationAbort(TxVersion version)
    {
        int incarnation = version.Incarnation;
        ref TxState state = ref _txStates[version.TxIndex];
        ref long stateInt = ref Unsafe.As<TxState, long>(ref state);
        TxState value = new(TxStatus.Aborting, incarnation);
        TxState requiredState = new(TxStatus.Executed, incarnation);
        long requiredInt = Unsafe.As<TxState, long>(ref requiredState);
        long valueInt = Unsafe.As<TxState, long>(ref value);
        return Interlocked.CompareExchange(ref stateInt, valueInt, requiredInt) == requiredInt;
    }

    /// <summary>Records a finished validation, possibly returning a re-execution task.</summary>
    public TxTask FinishValidation(int txIndex, bool aborted)
    {
        if (aborted)
        {
            SetReady(txIndex);
            DecreaseIndex(ref _validationIndex, txIndex + 1);

            // If the execution index already progressed past this tx, try to immediately
            // claim its re-execution instead of waiting for a worker to pick it up.
            if (Volatile.Read(ref _executionIndex) > txIndex)
            {
                TxVersion incarnation = TryIncarnate(txIndex, TxStatus.Ready, TxStatus.Executing, trackActiveTasks: false);
                if (!incarnation.IsEmpty)
                {
                    // Don't decrement _activeTasks — we're spawning a new task.
                    return new TxTask(incarnation, false);
                }
            }
        }

        Interlocked.Decrement(ref _activeTasks);
        CheckDone();
        return TxTask.Empty;
    }
}

/// <summary>Scheduler task: a transaction version, plus whether to validate or execute it.</summary>
public readonly record struct TxTask(TxVersion TxVersion, bool Validating)
{
    public static readonly TxTask Empty = new(TxVersion.Empty, false);
    public bool IsEmpty => TxVersion.IsEmpty;
    public override string ToString() => IsEmpty ? "Empty" : $"{(Validating ? "Validating" : "Executing")} {TxVersion}";
}

/// <summary>
/// Packed (Status, Incarnation) tuple. Stored as a 64-bit aligned struct so the scheduler
/// can update both fields with a single packed CAS via <c>Unsafe.As&lt;TxState, long&gt;</c>.
/// </summary>
[StructLayout(LayoutKind.Explicit)]
public struct TxState(int status, int incarnation)
{
    [FieldOffset(0)]
    public int Status = status;

    [FieldOffset(4)]
    public int Incarnation = incarnation;
}

public static class TxStatus
{
    public const int Ready = 0;
    public const int Executing = 1;
    public const int Executed = 2;
    public const int Aborting = 3;
}
