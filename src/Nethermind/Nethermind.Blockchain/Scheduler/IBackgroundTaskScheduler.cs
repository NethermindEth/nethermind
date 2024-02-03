// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.Blockchain.Scheduler;

/// <summary>
/// Provide interface to run task in background.
/// Task will be run in a separate thread.. well it depends on the threadpool, but there is a concurrency limit.
/// Task closure will have CancellationToken which will be cancelled if block processing happens while the task is running.
/// Task will still run when block processing is happening but its CancellationToken is cancelled. This is to provide
/// ways for the task to cleanly stop.
/// Task have a default timeout, which is counted from the time it is queued. If timedout because too many other background
/// task before it for example, the cancellation token passed to it will be cancelled. So it will still run, but the
/// cancellation token will be cancelled.
/// It is up to the task to determine what happen if cancelled, maybe it will reschedule for later, or resume later, but
/// preferably, stop execution immediately. Don't hang BTW. Other background task need to cancel too.
///
/// Note: Yes, I know there is a built in TaskScheduler that can do some magical stuff that stop execution on async
/// and stuff, but that is complicated and I don't wanna explain why you need `async Task.Yield()` in the middle of a loop,
/// or explicitly specify it to run on this task scheduler and such. Maybe some other time ok?
/// </summary>
public interface IBackgroundTaskScheduler
{
    void ScheduleTask<TReq>(TReq request, Func<TReq, CancellationToken, Task> fulfillFunc, TimeSpan? timeout = null);
}
