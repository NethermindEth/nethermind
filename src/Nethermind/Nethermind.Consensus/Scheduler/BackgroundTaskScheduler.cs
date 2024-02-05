// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Nethermind.Consensus.Processing;
using Nethermind.Core.Extensions;
using Nethermind.Logging;

namespace Nethermind.Consensus.Scheduler;

/// <summary>
/// Provide a way to orchestrate task to run in background.
/// - Task will be run in a separate thread.. well it depends on the threadpool, but there is a concurrency limit.
/// - Task closure will have CancellationToken which will be cancelled if block processing happens while the task is running.
/// - Task have a default timeout, which is counted from the time it is queued. If timedout because too many other background
///    task before it for example, the cancellation token passed to it will be cancelled.
/// - Task will still run when block processing is happening and its timedout this is so that it can handle its cancellation.
/// - Task will not run if block processing is happening and it still have some time left.
///   It is up to the task to determine what happen if cancelled, maybe it will reschedule for later, or resume later, but
///   preferably, stop execution immediately. Don't hang BTW. Other background task need to cancel too.
///
/// Note: Yes, I know there is a built in TaskScheduler that can do some magical stuff that stop execution on async
/// and stuff, but that is complicated and I don't wanna explain why you need `async Task.Yield()` in the middle of a loop,
/// or explicitly specify it to run on this task scheduler and such. Maybe some other time ok?
/// </summary>
public class BackgroundTaskScheduler : IBackgroundTaskScheduler, IAsyncDisposable
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(2);

    private readonly CancellationTokenSource _mainCancellationTokenSource;
    private CancellationTokenSource _blockProcessorCancellationTokenSource;
    private readonly Channel<IActivity> _taskQueue;
    private readonly ILogger _logger;
    private readonly IBlockProcessor _blockProcessor;
    private readonly ManualResetEvent _restartQueueSignal;

    public BackgroundTaskScheduler(IBlockProcessor blockProcessor, int concurrency, ILogManager logManager)
    {
        if (concurrency < 1) throw new ArgumentException("concurrency must be at least 1");

        _mainCancellationTokenSource = new CancellationTokenSource();
        _blockProcessorCancellationTokenSource = new CancellationTokenSource();
        _taskQueue = Channel.CreateUnbounded<IActivity>();
        _logger = logManager.GetClassLogger();
        _blockProcessor = blockProcessor;
        _restartQueueSignal = new ManualResetEvent(true);

        _blockProcessor.BlocksProcessing += BlockProcessorOnBlocksProcessing;
        _blockProcessor.BlockProcessed += BlockProcessorOnBlockProcessed;

        for (int i = 0; i < concurrency; i++)
        {
            Task.Factory.StartNew(StartChannel, TaskCreationOptions.LongRunning);
        }
    }

    private void BlockProcessorOnBlocksProcessing(object? sender, BlocksProcessingEventArgs e)
    {
        // On block processing, we cancel the block process cts, causing current task to get cancelled.
        _blockProcessorCancellationTokenSource.Cancel();
        // We also reset queue signal, causing it to wait
        _restartQueueSignal.Reset();
    }

    private void BlockProcessorOnBlockProcessed(object? sender, BlockProcessedEventArgs e)
    {
        // Once block is processed, we replace it with the
        CancellationTokenSource oldTokenSource = Interlocked.Exchange(ref _blockProcessorCancellationTokenSource, new CancellationTokenSource());
        oldTokenSource.Dispose();
        // We also set queue signal causing it to continue queue.
        _restartQueueSignal.Set();
    }


    private async Task StartChannel()
    {
        await foreach (IActivity activity in _taskQueue.Reader.ReadAllAsync(_mainCancellationTokenSource.Token))
        {
            if (_blockProcessorCancellationTokenSource.IsCancellationRequested)
            {
                // In case of task that is suppose to run when a block is being processed, if there is some time left
                // from its deadline, we re-queue it. We do this in case there are some task in the queue that already
                // reached deadline during block processing in which case, it will need to execute in order to handle
                // its cancellation.
                if (DateTimeOffset.Now < activity.Deadline)
                {
                    await _taskQueue.Writer.WriteAsync(activity, _mainCancellationTokenSource.Token);
                    // Throttle deque to prevent infinite loop.
                    await _restartQueueSignal.WaitOneAsync(TimeSpan.FromMilliseconds(1), _mainCancellationTokenSource.Token);
                    continue;
                }
            }

            try
            {
                using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(
                    _blockProcessorCancellationTokenSource.Token,
                    _mainCancellationTokenSource.Token
                );
                await activity.Do(cts.Token);
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

        if (!_taskQueue.Writer.TryWrite(activity))
        {
            // This should never happen unless something goes very wrong.
            throw new InvalidOperationException("Unable to write to background task queue.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        _blockProcessor.BlocksProcessing += BlockProcessorOnBlocksProcessing;
        _blockProcessor.BlockProcessed += BlockProcessorOnBlockProcessed;

        _taskQueue.Writer.Complete();
        await _mainCancellationTokenSource.CancelAsync();
    }

    private struct SyncActivity<TReq> : IActivity
    {
        public DateTimeOffset Deadline { get; init; }
        public TReq Request { get; init; }
        public Func<TReq, CancellationToken, Task> FulfillFunc { get; init; }

        public async Task Do(CancellationToken cancellationToken)
        {
            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            DateTimeOffset now = DateTimeOffset.Now;
            TimeSpan timeToComplete = Deadline - now;
            if (timeToComplete <= TimeSpan.Zero)
            {
                // Cancel immediately. Got no time left.
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
        DateTimeOffset Deadline { get; }
        Task Do(CancellationToken cancellationToken);
    }
}
