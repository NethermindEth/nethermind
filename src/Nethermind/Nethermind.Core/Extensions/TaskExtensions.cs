// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.Core.Extensions;

public static class TaskExtensions
{
    /// <summary>
    /// Delay that returns immediately when cancelled instead of throwing <see cref="OperationCanceledException"/>.
    /// Returns <c>true</c> if the delay completed normally, <c>false</c> if cancelled.
    /// </summary>
    public static async Task<bool> DelaySafe(int millisecondsDelay, CancellationToken cancellationToken)
    {
        try { await Task.Delay(millisecondsDelay, cancellationToken); return true; }
        catch (OperationCanceledException) { return false; }
    }

    /// <summary>
    /// Delay that returns immediately when cancelled instead of throwing <see cref="OperationCanceledException"/>.
    /// Returns <c>true</c> if the delay completed normally, <c>false</c> if cancelled.
    /// </summary>
    public static async Task<bool> DelaySafe(TimeSpan delay, CancellationToken cancellationToken)
    {
        try { await Task.Delay(delay, cancellationToken); return true; }
        catch (OperationCanceledException) { return false; }
    }

    public static bool IsFailedButNotCanceled(this Task? task)
    {
        if (task is null || !task.IsFaulted || task.Exception is null) return false;

        AggregateException exception = task.Exception.Flatten();

        // TaskCanceledException is a derived type of OperationCanceledException
        return exception.InnerException is not OperationCanceledException
            && exception.InnerExceptions.All(static ex => ex is not OperationCanceledException);
    }

    public static bool HasCanceledException(this Task? task)
    {
        if (task is null || !task.IsFaulted || task.Exception is null) return false;

        AggregateException exception = task.Exception.Flatten();

        // TaskCanceledException is a derived type of OperationCanceledException
        return exception.InnerException is OperationCanceledException
            || exception.InnerExceptions.Any(static ex => ex is OperationCanceledException);
    }

    public static bool HasTimeoutException(this Task? task)
    {
        if (task is null || !task.IsFaulted || task.Exception is null) return false;

        AggregateException exception = task.Exception.Flatten();

        return exception.InnerException is TimeoutException
            || exception.InnerExceptions.Any(static ex => ex is TimeoutException);
    }
}
