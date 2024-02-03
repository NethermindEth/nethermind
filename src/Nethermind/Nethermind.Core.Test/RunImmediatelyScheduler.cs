// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Consensus.Scheduler;

namespace Nethermind.Core.Test;

public class RunImmediatelyScheduler: IBackgroundTaskScheduler
{
    public static RunImmediatelyScheduler Instance = new RunImmediatelyScheduler();

    private RunImmediatelyScheduler()
    {
    }

    public void ScheduleTask<TReq>(TReq request, Func<TReq, CancellationToken, Task> fulfillFunc, TimeSpan? timeout = null)
    {
        fulfillFunc(request, CancellationToken.None);
    }
}
