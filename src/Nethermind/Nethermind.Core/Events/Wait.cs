// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.Core.Events;

public static class Wait
{
    public static async Task ForEventCondition<T>(
        CancellationToken cancellationToken,
        Action<EventHandler<T>> register,
        Action<EventHandler<T>> unregister,
        Func<T, bool> condition)
    {
        TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        void handler(object? sender, T t)
        {
            if (condition(t))
            {
                completion.TrySetResult();
            }
        }

        register(handler);

        try
        {
            await using (cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken)))
            {
                await completion.Task;
            }
        }
        finally
        {
            unregister(handler);
        }
    }

    public static async Task ForEvent(
        CancellationToken cancellationToken,
        Action<EventHandler> register,
        Action<EventHandler> unregister)
    {
        TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        void handler(object? sender, EventArgs e) => completion.TrySetResult();

        register(handler);

        try
        {
            await using (cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken)))
            {
                await completion.Task;
            }
        }
        finally
        {
            unregister(handler);
        }
    }

    /// <summary>Polls <paramref name="condition"/> until it returns true, the timeout elapses, or <paramref name="cancellationToken"/> is signaled.</summary>
    /// <returns><c>true</c> if the condition was met within the timeout; <c>false</c> on timeout.</returns>
    /// <exception cref="OperationCanceledException">Thrown if <paramref name="cancellationToken"/> is signaled before the condition is met.</exception>
    public static async Task<bool> ForCondition(
        Func<bool> condition,
        TimeSpan timeout,
        TimeSpan pollInterval = default,
        CancellationToken cancellationToken = default)
    {
        if (pollInterval == default) pollInterval = TimeSpan.FromMilliseconds(25);
        long deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        while (!condition())
        {
            if (Environment.TickCount64 >= deadline) return false;
            await Task.Delay(pollInterval, cancellationToken);
        }
        return true;
    }
}
