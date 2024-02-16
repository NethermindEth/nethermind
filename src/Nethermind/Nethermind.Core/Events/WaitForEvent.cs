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
        TaskCompletionSource completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<T> handler = (sender, t) =>
        {
            if (condition(t))
            {
                completion.TrySetResult();
            }
        };

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
}
