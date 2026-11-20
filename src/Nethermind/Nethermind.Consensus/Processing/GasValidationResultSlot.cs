// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core.Exceptions;

namespace Nethermind.Consensus.Processing;

public readonly record struct GasValidationResult(
    ulong BlockGasUsed,
    ulong BlockStateGasUsed,
    InvalidBlockException? Exception);

/// <summary>
/// One-shot result handoff from a parallel tx worker to the in-order validator.
/// </summary>
/// <remarks>
/// There is one slot per transaction, so a block of N transactions has N slots. Guarding each with
/// <c>lock</c> + <see cref="Monitor.PulseAll"/> inflated every slot's object header into a sync block, and
/// sync-block creation goes through a single process-wide lock: with many workers that lock, not tx
/// execution, dominated the parallel phase. Completion is published with an atomic state instead; the
/// validator spins briefly and only then parks on an event it creates lazily, so most slots never
/// allocate one.
/// </remarks>
public sealed class GasValidationResultSlot
{
    private const int Pending = 0;
    private const int Publishing = 1;
    private const int Completed = 2;

    private GasValidationResult _result;
    private Exception? _exception;
    private int _state;
    private ManualResetEventSlim? _waiter;

    /// <remarks>Called between blocks only, while no worker or validator uses the slot.</remarks>
    public void Reset()
    {
        _result = default;
        _exception = null;
        _waiter?.Reset();
        Volatile.Write(ref _state, Pending);
    }

    public bool TrySetResult(in GasValidationResult result)
    {
        if (Interlocked.CompareExchange(ref _state, Publishing, Pending) != Pending)
        {
            return false;
        }

        _result = result;
        _exception = null;
        Publish();
        return true;
    }

    public bool TrySetCanceled()
    {
        if (Interlocked.CompareExchange(ref _state, Publishing, Pending) != Pending)
        {
            return false;
        }

        _result = default;
        _exception = new TaskCanceledException();
        Publish();
        return true;
    }

    public GasValidationResult GetResult()
    {
        if (Volatile.Read(ref _state) != Completed)
        {
            WaitForCompletion();
        }

        if (_exception is not null)
        {
            ExceptionDispatchInfo.Capture(_exception).Throw();
        }

        return _result;
    }

    private void Publish()
    {
        // Full fence: the state store must be visible before reading _waiter, pairing with the
        // waiter's CompareExchange on _waiter followed by its state read (no lost wake-up).
        Interlocked.Exchange(ref _state, Completed);
        Volatile.Read(ref _waiter)?.Set();
    }

    private void WaitForCompletion()
    {
        SpinWait spinner = default;
        while (!spinner.NextSpinWillYield)
        {
            if (Volatile.Read(ref _state) == Completed) return;
            spinner.SpinOnce();
        }

        ManualResetEventSlim waiter = Volatile.Read(ref _waiter)
            ?? Interlocked.CompareExchange(ref _waiter, new ManualResetEventSlim(false), null)
            ?? _waiter!;
        while (Volatile.Read(ref _state) != Completed)
        {
            waiter.Wait();
        }
    }
}
