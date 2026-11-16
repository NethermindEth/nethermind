// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.JsonRpc.Exceptions;

namespace Nethermind.JsonRpc.Modules;

internal sealed class MethodConcurrencyLimit(int limit, int timeout)
{
    private readonly SemaphoreSlim _slots = new(limit);

    public Func<bool, CancellationToken, ValueTask<IRpcModule>> WrapRent(Func<bool, CancellationToken, ValueTask<IRpcModule>> rent) =>
        (canBeShared, cancellationToken) => _slots.Wait(0)
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
                _slots.Release();
            }
        };

    private async ValueTask<IRpcModule> WaitAndRent(Func<bool, CancellationToken, ValueTask<IRpcModule>> rent, bool canBeShared, CancellationToken cancellationToken)
    {
        if (!await _slots.WaitAsync(timeout, cancellationToken))
        {
            throw new ModuleRentalTimeoutException("Unable to start the call. Too many concurrent calls.");
        }

        if (cancellationToken.IsCancellationRequested)
        {
            _slots.Release();
            cancellationToken.ThrowIfCancellationRequested();
        }

        return await RentInSlot(rent, canBeShared, cancellationToken);
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
            _slots.Release();
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
            _slots.Release();
            throw;
        }
    }
}
