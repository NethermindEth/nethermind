// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Consensus.Scheduler;

namespace Nethermind.Core.Test;

public class RunImmediatelyScheduler : IBackgroundTaskScheduler
{
    public static RunImmediatelyScheduler Instance = new();

    private RunImmediatelyScheduler()
    {
    }

    public bool TryScheduleTask<TReq>(in TReq request, Func<TReq, CancellationToken, Task> fulfillFunc, TimeSpan? timeout = null) where TReq : notnull
    {
        fulfillFunc(request, CancellationToken.None);
        return true;
    }
}
