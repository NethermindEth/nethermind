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
        void Handler(object? sender, T t)
        {
            if (condition(t))
            {
                completion.TrySetResult();
            }
        }

        register(Handler);

        try
        {
            await using (cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken)))
            {
                await completion.Task;
            }
        }
        finally
        {
            unregister(Handler);
        }
    }

    public static async Task ForEvent(
        CancellationToken cancellationToken,
        Action<EventHandler> register,
        Action<EventHandler> unregister)
    {
        TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(object? sender, EventArgs e)
        {
            completion.TrySetResult();
        }

        register(Handler);

        try
        {
            await using (cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken)))
            {
                await completion.Task;
            }
        }
        finally
        {
            unregister(Handler);
        }
    }
}
