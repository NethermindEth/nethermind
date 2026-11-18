// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.Consensus.Scheduler;

public interface IBackgroundTaskScheduler
{
    /// <summary>The configured queued-task capacity, or null when the implementation does not expose it.</summary>
    /// <remarks>The default uses bounded network compatibility quotas without a scheduler-wide identity guarantee and prevents startup validation of the resolved capacity. Decorators must forward the wrapped capacity.</remarks>
    int? QueueCapacity => null;

    bool TryScheduleTask<TReq>(TReq request, Func<TReq, CancellationToken, Task> fulfillFunc, TimeSpan? timeout = null)
        where TReq : notnull, IBackgroundTaskRequest<TReq>;
}
