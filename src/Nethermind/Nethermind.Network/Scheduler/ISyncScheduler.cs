// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.Network.Scheduler;

public interface IBackgroundTaskScheduler
{
    void ScheduleTask<TReq>(TReq request, Func<TReq, CancellationToken, Task> fulfillFunc);
}
