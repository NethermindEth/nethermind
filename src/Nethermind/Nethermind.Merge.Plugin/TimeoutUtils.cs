// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;

namespace Nethermind.Merge.Plugin;

public static class TimeoutUtils
{
    public static async Task<T> TimeoutOn<T>(this Task<T> task, Task timeoutTask)
    {
        Task firstToComplete = await Task.WhenAny(timeoutTask, task);
        if (firstToComplete == timeoutTask)
        {
            throw new TimeoutException();
        }

        return await task;
    }
}
