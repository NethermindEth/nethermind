// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using Microsoft.Extensions.ObjectPool;
using Nethermind.Core.Threading;

namespace Nethermind.Consensus.Processing.ParallelProcessing;

public class ParallelScheduler<TLogger>(ushort blockSize, ParallelTrace<TLogger> parallelTrace, ObjectPool<HashSet<ushort>> setPool) where TLogger : struct, IIsTracing
{
    private int _executionIndex;
    private int _validationIndex;
    private int _decreaseCount;
    private int _activeTasks;
    private readonly TxState[] _txStates = new TxState[blockSize];
    private readonly HashSet<ushort>[] _txDependencies = Enumerable.Range(0, blockSize).Select(_ => setPool.Get()).ToArray();

    public bool Done { get; private set; } = false;

    private void DecreaseIndex(ref int index, int targetIndex, string name)
    {
        long id = parallelTrace.ReserveId();
        int value = InterlockedEx.MutateValue(ref index, targetIndex, static (current, target) => Math.Min(current, target));
        int decreaseCount = Interlocked.Increment(ref _decreaseCount);
        if (typeof(TLogger) == typeof(IsTracing)) parallelTrace.Add(id, $"Decreased {name} index to {value}, decrease count: {decreaseCount}");
    }

    private void CheckDone()
    {
        int observedCount = Volatile.Read(ref _decreaseCount);
        bool done = Math.Min(_executionIndex, _validationIndex) >= blockSize
                   && Volatile.Read(ref _activeTasks) == 0 && observedCount == Volatile.Read(ref _decreaseCount);
        Done |= done;
        if (typeof(TLogger) == typeof(IsTracing) && done) parallelTrace.Add("Done");
    }

    private Version FetchNext(ref int index, ushort requiredStatus, ushort newStatus)
    {
        if (index >= blockSize)
        {
            CheckDone();
            return Version.Empty;
        }

        Interlocked.Increment(ref _activeTasks);
        int nextTx = Interlocked.Increment(ref index) - 1;
        return TryIncarnate(nextTx, requiredStatus, newStatus);
    }

    private Version TryIncarnate(int nextTx, ushort requiredStatus, ushort newStatus)
    {
        if (nextTx < blockSize)
        {
            ref TxState state = ref _txStates[nextTx];
            if (Interlocked.CompareExchange(ref state.Status, newStatus, requiredStatus) == requiredStatus)
            {
                if (typeof(TLogger) == typeof(IsTracing)) parallelTrace.Add($"Set Tx {nextTx} status to {TxStatus.GetName(requiredStatus)}");
                return new Version((ushort)nextTx, state.Incarnation);
            }
        }

        Interlocked.Decrement(ref _activeTasks);
        return Version.Empty;
    }

    public TxTask NextTask()
    {
        bool validating = Volatile.Read(ref _validationIndex) < Volatile.Read(ref _executionIndex);
        return new TxTask(validating
                ? FetchNext(ref _validationIndex, TxStatus.Executed, TxStatus.Executed)
                : FetchNext(ref _executionIndex, TxStatus.Ready, TxStatus.Executing),
            validating);
    }

    public bool AddDependency(ushort txIndex, ushort blockingTxIndex)
    {
        ref TxState blockingTxState = ref _txStates[blockingTxIndex];
        ushort blockingTxStatus = Volatile.Read(ref blockingTxState.Status);
        if (blockingTxStatus == TxStatus.Executed)
        {
            if (typeof(TLogger) == typeof(IsTracing)) parallelTrace.Add($"Can't add dependency for tx {txIndex} on {blockingTxIndex}, because it is already executed");
            return false;
        }

        if (typeof(TLogger) == typeof(IsTracing)) parallelTrace.Add($"Adding dependency for tx {txIndex} on {blockingTxIndex}, Tx {blockingTxIndex} status is {TxStatus.GetName(blockingTxStatus)}");
        ref TxState txState = ref _txStates[txIndex];
        Interlocked.Exchange(ref txState.Status, TxStatus.Aborting);
        if (typeof(TLogger) == typeof(IsTracing)) parallelTrace.Add($"Set Tx {txIndex} status to Aborting");

        HashSet<ushort> set = Volatile.Read(ref _txDependencies[blockingTxIndex]);
        lock (set)
        {
            set.Add(txIndex);
        }

        if (typeof(TLogger) == typeof(IsTracing)) parallelTrace.Add($"Dependency added for tx {txIndex} on {blockingTxIndex} to set {set.GetHashCode()}");

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

        Interlocked.Decrement(ref _activeTasks);
        return true;
    }

    private void ResumeDependencies(HashSet<ushort> dependentTxs, ushort blockingTxIndex)
    {
        if (dependentTxs.Count > 0)
        {
            ushort min = ushort.MaxValue;
            lock (dependentTxs)
            {
                foreach (ushort tx in dependentTxs)
                {
                    min = Math.Min(min, tx);
                    SetReady(tx);
                }

                if (typeof(TLogger) == typeof(IsTracing)) parallelTrace.Add($"Resumed dependencies by Tx {blockingTxIndex}: {string.Join(", ", dependentTxs)}");
                dependentTxs.Clear();
                setPool.Return(dependentTxs);
            }

            if (min != ushort.MaxValue)
            {
                DecreaseIndex(ref _executionIndex, min, "execution");
            }

        }
    }

    private void SetReady(ushort txIndex)
    {
        ref TxState state = ref _txStates[txIndex];
        Interlocked.Exchange(ref state.Status, TxStatus.Ready);
        if (typeof(TLogger) == typeof(IsTracing)) parallelTrace.Add($"Set Tx {txIndex} status to Ready");
        state.Incarnation++;
    }

    public TxTask FinishExecution(Version version, bool wroteNewLocation)
    {
        ushort txIndex = version.TxIndex;
        ref TxState state = ref _txStates[txIndex];
        Interlocked.Exchange(ref state.Status, TxStatus.Executed);
        if (typeof(TLogger) == typeof(IsTracing)) parallelTrace.Add($"Set Tx {txIndex} status to Executed");

        HashSet<ushort> dependencies = Interlocked.Exchange(ref _txDependencies[txIndex], setPool.Get());
        ResumeDependencies(dependencies, txIndex);

        if (Volatile.Read(ref _validationIndex) > txIndex)
        {
            if (!wroteNewLocation)
            {
                return new TxTask(version, true);
            }

            DecreaseIndex(ref _validationIndex, txIndex, "validation");
        }

        Interlocked.Decrement(ref _activeTasks);
        return new TxTask(Version.Empty, false);
    }

    public bool TryValidationAbort(Version version)
    {
        ref TxState state = ref _txStates[version.TxIndex];
        ref int stateInt = ref Unsafe.As<TxState, int>(ref state);
        TxState value = new TxState(TxStatus.Aborting, version.Incarnation);
        TxState requiredState = new TxState(TxStatus.Executed, version.Incarnation);
        int requiredInt = Unsafe.As<TxState, int>(ref requiredState);
        return Interlocked.CompareExchange(ref stateInt, Unsafe.As<TxState, int>(ref value), requiredInt) == requiredInt;
    }

    public TxTask FinishValidation(ushort txIndex, bool aborted)
    {
        if (aborted)
        {
            SetReady(txIndex);
            DecreaseIndex(ref _validationIndex, txIndex + 1, "validation");
            if (Volatile.Read(ref _executionIndex) > txIndex)
            {
                return new TxTask(TryIncarnate(txIndex, TxStatus.Ready, TxStatus.Executing), false);
            }
        }

        Interlocked.Decrement(ref _activeTasks);
        return new TxTask(Version.Empty, false);
    }
}

public readonly record struct TxTask(Version Version, bool Validating)
{
    public bool IsEmpty => Version.IsEmpty;
    public override string ToString() => IsEmpty ? "Empty" : $"{(Validating ? "Validating" : "Executing")} {Version}";
}

public record struct TxState(ushort Status, ushort Incarnation)
{
    public ushort Status = Status;
    public ushort Incarnation = Incarnation;
}

public static class TxStatus
{
    public const ushort Ready = 0;
    public const ushort Executing = 1;
    public const ushort Executed = 2;
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
