// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.Core.Threading;

/// <inheritdoc cref="ParallelUnbalancedWork"/>
public partial class ParallelUnbalancedWork
{
    // One worker, the calling thread. The order of checks mirrors the threaded worker: cancellation
    // stops the loop before the next action, a failure is reported before cancellation is, and the
    // finalizer runs whenever initialization did.
    private static partial void ForCore(int fromInclusive, int toExclusive, ParallelOptions parallelOptions, Action<int> action)
    {
        CancellationToken token = parallelOptions.CancellationToken;
        token.ThrowIfCancellationRequested();

        for (int i = fromInclusive; i < toExclusive && !token.IsCancellationRequested; i++)
        {
            action(i);
        }

        token.ThrowIfCancellationRequested();
    }

    private static partial void ForCore<TLocal>(
        int fromInclusive,
        int toExclusive,
        ParallelOptions parallelOptions,
        Func<TLocal>? init,
        TLocal? initValue,
        Func<int, TLocal, TLocal> action,
        Action<TLocal>? @finally)
    {
        CancellationToken token = parallelOptions.CancellationToken;
        token.ThrowIfCancellationRequested();
        if (toExclusive <= fromInclusive) return;

        TLocal value = init is not null ? init() : initValue!;
        Exception? fault = null;
        try
        {
            for (int i = fromInclusive; i < toExclusive && !token.IsCancellationRequested; i++)
            {
                value = action(i, value);
            }
        }
        catch (Exception ex)
        {
            fault = ex;
        }

        try
        {
            @finally?.Invoke(value);
        }
        catch (Exception ex)
        {
            fault ??= ex;
        }

        if (fault is not null) ExceptionDispatchInfo.Throw(fault);
        token.ThrowIfCancellationRequested();
    }
}
