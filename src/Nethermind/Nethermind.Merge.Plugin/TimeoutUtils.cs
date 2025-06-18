// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.Merge.Plugin;

public static class TimeoutUtils
{
    public static async Task<T> TimeoutOn<T>(this Task<T> task, Task timeoutTask, CancellationTokenSource? tcs = null)
    {
        Task firstToComplete = await Task.WhenAny(timeoutTask, task);
        if (firstToComplete == timeoutTask)
        {
            ThrowTimeout();
        }

        tcs?.Cancel();

        return await task;
    }

    [StackTraceHidden, DoesNotReturn]
    private static void ThrowTimeout() => throw new TimeoutException();
}
