// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.JsonRpc.Exceptions;

namespace Nethermind.JsonRpc.Modules;

internal sealed class MethodConcurrencyLimit(int limit, int timeout, RpcLimits limits)
{
    private readonly SemaphoreSlim _released = new(0);
    private int _running;
    private int _waiting;

    public MethodConcurrencyLimit(int limit, int timeout) : this(limit, timeout, RpcLimits.Default)
    {
    }

    public Func<bool, CancellationToken, ValueTask<IRpcModule>> WrapRent(Func<bool, CancellationToken, ValueTask<IRpcModule>> rent) =>
        (canBeShared, cancellationToken) => TryEnter()
            ? RentInSlot(rent, canBeShared, cancellationToken)
            : WaitAndRent(rent, canBeShared, cancellationToken);

    public Action<IRpcModule> WrapReturn(Action<IRpcModule> returnModule) =>
        module =>
        {
            try
            {
                returnModule(module);
            }
            finally
            {
                Exit();
            }
        };

    private bool TryEnter()
    {
        int running = Volatile.Read(ref _running);
        while (running < limit)
        {
            int seen = Interlocked.CompareExchange(ref _running, running + 1, running);
            if (seen == running) return true;
            running = seen;
        }

        return false;
    }

    private void Exit()
    {
        Interlocked.Decrement(ref _running);
        if (Volatile.Read(ref _waiting) > 0) _released.Release();
    }

    private async ValueTask<IRpcModule> WaitAndRent(Func<bool, CancellationToken, ValueTask<IRpcModule>> rent, bool canBeShared, CancellationToken cancellationToken)
    {
        limits.AcquireQueuedSlot();
        try
        {
            await WaitForSlot(cancellationToken);
        }
        finally
        {
            limits.DecrementQueuedCalls();
        }

        if (cancellationToken.IsCancellationRequested)
        {
            Exit();
            cancellationToken.ThrowIfCancellationRequested();
        }

        return await RentInSlot(rent, canBeShared, cancellationToken);
    }

    private async ValueTask WaitForSlot(CancellationToken cancellationToken)
    {
        long start = Stopwatch.GetTimestamp();
        Interlocked.Increment(ref _waiting);
        try
        {
            while (!TryEnter())
            {
                int remaining = timeout < 0 ? Timeout.Infinite : timeout - (int)Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                if ((timeout >= 0 && remaining <= 0) || !await _released.WaitAsync(remaining, cancellationToken))
                {
                    throw new ModuleRentalTimeoutException("Unable to start the call. Too many concurrent calls.");
                }
            }
        }
        finally
        {
            Interlocked.Decrement(ref _waiting);
        }
    }

    private ValueTask<IRpcModule> RentInSlot(Func<bool, CancellationToken, ValueTask<IRpcModule>> rent, bool canBeShared, CancellationToken cancellationToken)
    {
        ValueTask<IRpcModule> rental;
        try
        {
            rental = rent(canBeShared, cancellationToken);
        }
        catch
        {
            Exit();
            throw;
        }

        return rental.IsCompletedSuccessfully ? rental : AwaitRental(rental);
    }

    private async ValueTask<IRpcModule> AwaitRental(ValueTask<IRpcModule> rental)
    {
        try
        {
            return await rental;
        }
        catch
        {
            Exit();
            throw;
        }
    }
}
