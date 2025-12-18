// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
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
    public static async Task<T> AnyWhere<T>(Func<T, bool> cond, params IEnumerable<Task<T>> tasks)
    {
        HashSet<Task<T>> taskSet = new HashSet<Task<T>>(tasks);
        while (taskSet.Count != 0)
        {
            Task<T> resolved = await Task.WhenAny<T>(taskSet);
            taskSet.Remove(resolved);

            T result = await resolved;

            if (cond(result))
            {
                // Its ok, then immediately return.
                return result;
            }

            if (taskSet.Count == 0)
            {
                // No more tasks, just return the last one.
                return result;
            }

            // Otherwise, we try WhenAny again.
        }

        throw new UnreachableException();
    }
}
