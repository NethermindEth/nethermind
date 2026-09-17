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
    private static partial BackgroundWork BackgroundForCore(int fromInclusive, int toExclusive,
        ParallelOptions options, Action<int> action, Action? completed)
    {
        options.CancellationToken.ThrowIfCancellationRequested();
        return new(fromInclusive, toExclusive, options.CancellationToken, action, completed);
    }

    public sealed partial class BackgroundWork(int from, int to, CancellationToken token, Action<int> action, Action? completed)
    {
        private bool _joined;
        private ExceptionDispatchInfo? _exception;

        public partial void WaitForCompletion()
        {
            Dispose();
            _exception?.Throw();
            token.ThrowIfCancellationRequested();
        }

        public partial void Dispose()
        {
            if (_joined) return;
            _joined = true;
            try
            {
                for (int i = from; i < to && !token.IsCancellationRequested; i++) action(i);
                if (!token.IsCancellationRequested) completed?.Invoke();
            }
            catch (Exception ex)
            {
                _exception = ExceptionDispatchInfo.Capture(ex);
            }
        }
    }
}
