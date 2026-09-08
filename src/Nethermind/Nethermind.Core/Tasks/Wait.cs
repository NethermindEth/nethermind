// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.Core.Tasks;

public static class Wait
{
    /// <summary>
    /// Wait for any of the task that passed the predicate and forward the result, or all of the task to complete.
    /// </summary>
    /// <param name="cond"></param>
    /// <param name="tasks"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    /// <remarks>
    /// Only the forwarded result reaches the caller; every other result stays owned by this method.
    /// When <typeparamref name="T"/> is <see cref="IDisposable"/> those results are disposed — one
    /// rejected by <paramref name="cond"/> as soon as it is seen, and the results of the tasks still
    /// in flight once a result is forwarded once they complete. Forwarding does not wait for those,
    /// so their disposal happens after this method returns.
    /// </remarks>
    public static async Task<T> AnyWhere<T>(Func<T, bool> cond, params IEnumerable<Task<T>> tasks)
    {
        HashSet<Task<T>> taskSet = [.. tasks];
        while (taskSet.Count != 0)
        {
            Task<T> resolved = await Task.WhenAny<T>(taskSet);
            taskSet.Remove(resolved);

            T result = await resolved;

            if (cond(result))
            {
                // Its ok, then immediately return.
                DiscardRemaining(taskSet);
                return result;
            }

            if (taskSet.Count == 0)
            {
                // No more tasks, just return the last one.
                return result;
            }

            Discard(result);

            // Otherwise, we try WhenAny again.
        }

        throw new UnreachableException();
    }

    private static void Discard<T>(T result)
    {
        if (result is IDisposable disposable) disposable.Dispose();
    }

    private static void DiscardRemaining<T>(HashSet<Task<T>> tasks)
    {
        if (!typeof(IDisposable).IsAssignableFrom(typeof(T))) return;

        foreach (Task<T> task in tasks)
        {
            _ = task.ContinueWith(static abandoned =>
            {
                if (abandoned.IsCompletedSuccessfully) Discard(abandoned.Result);
                // Observe a failure too, so abandoning it does not raise UnobservedTaskException.
                else _ = abandoned.Exception;
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }
}
