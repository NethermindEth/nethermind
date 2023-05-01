// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Threading.Tasks;

namespace Nethermind.Network.Extensions;
public static class TaskExtensions
{
    public static bool IsFailedButNotCancelled(this Task? task)
    {
        if (task is null || task.IsFaulted || task.Exception is null) return false;

        AggregateException exception = task.Exception.Flatten();

        return exception.InnerException is not TaskCanceledException
            && exception.InnerExceptions.All(ex => ex is not TaskCanceledException);
    }
}
