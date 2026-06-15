// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.Core.Extensions;

/// <summary>
/// Synchronous, blocking consumers for <see cref="ValueTask"/> / <see cref="ValueTask{TResult}"/> that honor the
/// single-consumption contract of <see cref="System.Threading.Tasks.Sources.IValueTaskSource"/>-backed value tasks.
/// </summary>
/// <remarks>
/// Prevents <see cref="InvalidOperationException"/> when using <c>.GetAwaiter().GetResult()</c> on a non-completed value task,
/// while removing allocation of <c>.AsTask().GetAwaiter().GetResult()</c> approach.
/// </remarks>
public static class ValueTaskWaitExtensions
{
    [ThreadStatic]
    private static Gate? _gate;

    /// <summary>Blocks the current thread until <paramref name="valueTask"/> completes.</summary>
    /// <param name="valueTask">The value task to wait on. Must not have been consumed yet.</param>
    public static void SafeWait(this ValueTask valueTask)
    {
        ValueTaskAwaiter awaiter = valueTask.GetAwaiter();
        if (awaiter.IsCompleted)
        {
            awaiter.GetResult();
            return;
        }

        Gate gate = RentResetGate();
        awaiter.UnsafeOnCompleted(gate.Signal);
        gate.Event.Wait();
        awaiter.GetResult();
    }

    /// <summary>Blocks the current thread until <paramref name="valueTask"/> completes, discarding its result.</summary>
    /// <param name="valueTask">The value task to wait on. Must not have been consumed yet.</param>
    public static void SafeWait<TResult>(this ValueTask<TResult> valueTask) => valueTask.SafeGetResult();

    /// <summary>Blocks the current thread until <paramref name="valueTask"/> completes, then returns its result.</summary>
    /// <param name="valueTask">The value task to wait on. Must not have been consumed yet.</param>
    public static TResult SafeGetResult<TResult>(this ValueTask<TResult> valueTask)
    {
        ValueTaskAwaiter<TResult> awaiter = valueTask.GetAwaiter();
        if (awaiter.IsCompleted)
            return awaiter.GetResult();

        Gate gate = RentResetGate();
        awaiter.UnsafeOnCompleted(gate.Signal);
        gate.Event.Wait();
        return awaiter.GetResult();
    }

    private static Gate RentResetGate()
    {
        Gate gate = _gate ??= new Gate();
        gate.Event.Reset();
        return gate;
    }

    private sealed class Gate
    {
        public readonly ManualResetEventSlim Event = new(initialState: false);
        public readonly Action Signal;

        public Gate() => Signal = Event.Set;
    }
}
