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
    internal static partial int GetWorkerCount(int fromInclusive, int toExclusive, ParallelOptions parallelOptions)
    {
        parallelOptions.CancellationToken.ThrowIfCancellationRequested();
        return fromInclusive < toExclusive ? 1 : 0;
    }

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
        private bool _abandoned;
        private ExceptionDispatchInfo? _exception;
        private BackgroundWork? _dependency;
        private BackgroundWork? _continuation;

        private partial BackgroundWork ContinueWithCore(int fromInclusive, int toExclusive, ParallelOptions options,
            Action<int> action, Action? completed)
        {
            ObjectDisposedException.ThrowIf(_abandoned, this);
            if (_continuation is not null) throw new InvalidOperationException("A continuation is already attached.");
            options.CancellationToken.ThrowIfCancellationRequested();
            return _continuation = new(fromInclusive, toExclusive, options.CancellationToken, action, completed)
            {
                _dependency = this
            };
        }

        public partial void WaitForCompletion()
        {
            ObjectDisposedException.ThrowIf(_abandoned, this);
            if (_joined)
            {
                _exception?.Throw();
                token.ThrowIfCancellationRequested();
                return;
            }
            _joined = true;
            try
            {
                _dependency?.WaitForCompletion();
                for (int i = from; i < to && !token.IsCancellationRequested; i++) action(i);
                if (!token.IsCancellationRequested) completed?.Invoke();
            }
            catch (Exception ex)
            {
                _exception = ExceptionDispatchInfo.Capture(ex);
            }
            finally
            {
                action = null!;
                completed = null;
            }
            _exception?.Throw();
            token.ThrowIfCancellationRequested();
        }

        public partial void Dispose()
        {
            if (!_joined) _abandoned = true;
            action = null!;
            completed = null;
            _dependency?.Dispose();
        }
    }
}
