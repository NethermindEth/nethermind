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
    /// Results implementing <see cref="IDisposable"/> are disposed when rejected by <paramref name="cond"/>.
    /// Tasks abandoned on return or failure have their results disposed when they complete.
    /// This method does not wait for abandoned tasks, so their disposal may happen after it returns.
    /// </remarks>
    public static async Task<T> AnyWhere<T>(Func<T, bool> cond, params IEnumerable<Task<T>> tasks)
    {
        HashSet<Task<T>> taskSet = [.. tasks];
        try
        {
            while (taskSet.Count != 0)
            {
                Task<T> resolved = await Task.WhenAny<T>(taskSet);
                T result = await resolved;

                // Kept in the set until forwarded, so a throwing `cond` still discards this result.
                if (cond(result) || taskSet.Count == 1)
                {
                    taskSet.Remove(resolved);
                    return result;
                }

                taskSet.Remove(resolved);
                Discard(result);
            }

            throw new UnreachableException();
        }
        finally
        {
            DiscardRemaining(taskSet);
        }
    }

    private static void Discard<T>(T result)
    {
        if (result is IDisposable disposable) disposable.Dispose();
    }

    private static void DiscardRemaining<T>(HashSet<Task<T>> tasks)
    {
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
