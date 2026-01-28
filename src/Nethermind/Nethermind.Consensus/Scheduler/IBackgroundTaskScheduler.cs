// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.Consensus.Scheduler;

public interface IBackgroundTaskScheduler
{
    bool TryScheduleTask<TReq>(in TReq request, Func<TReq, CancellationToken, Task> fulfillFunc, TimeSpan? timeout = null) where TReq : notnull;
}
