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
    // Have some high limit, to prevent OOM.
    private const int BackgroundTaskHardLimit = 128 * 1024;
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(2);

    private readonly CancellationTokenSource _cancellationTokenSource;
    private readonly Channel<IActivity> _activities;
    private readonly ILogger _logger;

    public BackgroundTaskScheduler(int concurrency, ILogManager logManager)
    {
        _cancellationTokenSource = new CancellationTokenSource();
        _activities = Channel.CreateBounded<IActivity>(BackgroundTaskHardLimit);
        _logger = logManager.GetClassLogger();

        for (int i = 0; i < concurrency; i++)
        {
            Task.Factory.StartNew(() => StartChannel(_cancellationTokenSource.Token), TaskCreationOptions.LongRunning);
        }
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

    public void ScheduleTask<TReq>(TReq request, Func<TReq, CancellationToken, Task> fulfillFunc, TimeSpan? timeout = null)
    {
        timeout ??= DefaultTimeout;
        DateTimeOffset deadline = DateTimeOffset.Now + timeout.Value;

        IActivity activity = new SyncActivity<TReq>()
        {
            Deadline = deadline,
            Request = request,
            FulfillFunc = fulfillFunc,
        };

        if (!_activities.Writer.TryWrite(activity))
        {
            // This should never happen unless something goes very wrong.
            throw new InvalidOperationException("Unable to write to background task queue.");
        }
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
                await cts.CancelAsync();
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
