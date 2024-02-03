// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Nethermind.Logging;

namespace Nethermind.Network.Scheduler;

public class BackgroundTaskScheduler: IBackgroundTaskScheduler, IAsyncDisposable
{
    private readonly CancellationTokenSource _cancellationTokenSource;
    private readonly Channel<IActivity> _activities;
    private readonly ILogger _logger;

    public BackgroundTaskScheduler(ILogManager logManager)
    {
        _cancellationTokenSource = new CancellationTokenSource();
        _activities = Channel.CreateBounded<IActivity>(1024);
        _logger = logManager.GetClassLogger();

        Task.Factory.StartNew(() => StartChannel(_cancellationTokenSource.Token), TaskCreationOptions.LongRunning);
    }

    private async Task StartChannel(CancellationToken cancellationToken)
    {
        await foreach (IActivity activity in _activities.Reader.ReadAllAsync(cancellationToken))
        {
            try
            {
                await activity.Do(cancellationToken);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception e)
            {
                if (_logger.IsDebug) _logger.Debug($"Error processing background task {e}.");
            }
        }
    }

    public void ScheduleTask<TReq>(TReq request, Func<TReq, CancellationToken, Task> fulfillFunc)
    {
        DateTimeOffset deadline = DateTimeOffset.Now + TimeSpan.FromSeconds(2);

        IActivity activity = new SyncActivity<TReq>()
        {
            Deadline = deadline,
            Request = request,
            FulfillFunc = fulfillFunc,
        };

        _activities.Writer.TryWrite(activity);
    }

    public async ValueTask DisposeAsync()
    {
        _activities.Writer.Complete();
        await _cancellationTokenSource.CancelAsync();
    }

    private struct SyncActivity<TReq>: IActivity
    {
        public DateTimeOffset Deadline { get; init; }
        public TReq Request { get; init; }
        public Func<TReq, CancellationToken, Task> FulfillFunc { get; init; }

        public async Task Do(CancellationToken cancellationToken)
        {
            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            DateTimeOffset now = DateTimeOffset.Now;
            TimeSpan timeToComplete = Deadline - now;
            if (timeToComplete < TimeSpan.Zero)
            {
                cts.Cancel();
            }
            else
            {
                cts.CancelAfter(timeToComplete);
            }

            await FulfillFunc.Invoke(Request, cts.Token);
        }
    }

    private interface IActivity
    {
        Task Do(CancellationToken cancellationToken);
    }
}
