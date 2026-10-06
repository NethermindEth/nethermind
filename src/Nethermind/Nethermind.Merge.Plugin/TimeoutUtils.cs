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
        // WhenAny picks the first of its arguments when both are done, so a result already in hand wins over a timeout
        // that elapsed meanwhile.
        Task firstToComplete = await Task.WhenAny(task, timeoutTask);
        if (firstToComplete != task)
        {
            ThrowTimeout();
        }

        tcs?.Cancel();

        return await task;
    }

    [StackTraceHidden, DoesNotReturn]
    private static void ThrowTimeout() => throw new TimeoutException();
}
