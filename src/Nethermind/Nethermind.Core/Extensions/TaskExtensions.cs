// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Threading.Tasks;

namespace Nethermind.Core.Extensions;

public static class TaskExtensions
{
    public static bool IsFailedButNotCanceled(this Task? task)
    {
        if (task is null || task.IsFaulted || task.Exception is null) return false;

        AggregateException exception = task.Exception.Flatten();

        // TaskCanceledException is a derived type of OperationCanceledException
        return exception.InnerException is not OperationCanceledException
            && exception.InnerExceptions.All(ex => ex is not OperationCanceledException);
    }

    public static bool HasCanceledException(this Task? task)
    {
        if (task is null || task.IsFaulted || task.Exception is null) return false;

        AggregateException exception = task.Exception.Flatten();

        // TaskCanceledException is a derived type of OperationCanceledException
        return exception.InnerException is OperationCanceledException
            && exception.InnerExceptions.Any(ex => ex is OperationCanceledException);
    }

    public static bool HasTimeoutException(this Task? task)
    {
        if (task is null || task.IsFaulted || task.Exception is null) return false;

        AggregateException exception = task.Exception.Flatten();

        return exception.InnerException is TimeoutException
            && exception.InnerExceptions.Any(ex => ex is TimeoutException);
    }
}
