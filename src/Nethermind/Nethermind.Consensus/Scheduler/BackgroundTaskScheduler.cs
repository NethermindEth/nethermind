// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Nethermind.Consensus.Processing;
using Nethermind.Core.Extensions;
using Nethermind.Logging;
using Nethermind.TxPool;

namespace Nethermind.Consensus.Scheduler;

/// <summary>
/// Provide a way to orchestrate tasks to run in background at a lower priority.
/// - Task will be run in a lower priority thread, but there is a concurrency limit.
/// - Task closure will have CancellationToken which will be cancelled if block processing happens while the task is running.
/// - Task have a default timeout, which is counted from the time it is queued. If timed out because too many other background
///    task before it for example, the cancellation token passed to it will be cancelled.
/// - Task will still run when block processing is happening and its timed out this is so that it can handle its cancellation.
/// - Task will not run if block processing is happening and it still have some time left.
///   It is up to the task to determine what happen if cancelled, maybe it will reschedule for later, or resume later, but
///   preferably, stop execution immediately. Don't hang BTW. Other background task need to cancel too.
/// - A failure at this level is considered unexpected and loud. Exception should be handled at handler level.
/// </summary>
public class BackgroundTaskScheduler : IBackgroundTaskScheduler, IAsyncDisposable
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(2);

    private readonly CancellationTokenSource _mainCancellationTokenSource;
    private readonly Channel<IActivity> _taskQueue;
    private readonly Lock _queueLock = new();
    private readonly BelowNormalPriorityTaskScheduler _scheduler;
    private readonly ManualResetEventSlim _restartQueueSignal;
    private readonly Task[] _tasksExecutors;
    private readonly ILogger _logger;
    private readonly IBlockProcessor _blockProcessor;
    private readonly IChainHeadInfoProvider _headInfo;
    private readonly int _capacity;
    private long _queueCount;

    private CancellationTokenSource _blockProcessorCancellationTokenSource;

    public BackgroundTaskScheduler(IBlockProcessor blockProcessor, IChainHeadInfoProvider headInfo, int concurrency, int capacity, ILogManager logManager)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(concurrency, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);

        _mainCancellationTokenSource = new CancellationTokenSource();
        _blockProcessorCancellationTokenSource = new CancellationTokenSource();

        // In priority order, so if we reach an activity with time left,
        // we know rest still have time left
        _taskQueue = Channel.CreateUnboundedPrioritized<IActivity>();
        _logger = logManager.GetClassLogger();
        _blockProcessor = blockProcessor;
        _headInfo = headInfo;
        _restartQueueSignal = new ManualResetEventSlim(initialState: true);
        _capacity = capacity;

        _blockProcessor.BlocksProcessing += BlockProcessorOnBlocksProcessing;
        _blockProcessor.BlockProcessed += BlockProcessorOnBlockProcessed;

        // TaskScheduler to run tasks at BelowNormal priority
        _scheduler = new BelowNormalPriorityTaskScheduler(
            concurrency,
            _restartQueueSignal,
            logManager,
            _mainCancellationTokenSource.Token);

        TaskFactory factory = new(_scheduler);
        _tasksExecutors = [.. Enumerable.Range(0, concurrency).Select(_ => factory.StartNew(StartChannel).Unwrap())];
    }

    private void BlockProcessorOnBlocksProcessing(object? sender, BlocksProcessingEventArgs e)
    {
        // If we are syncing we don't block background task processing
        // as there are potentially no gaps between blocks
        if (!_headInfo.IsSyncing)
        {
            // Reset background queue processing signal, causing it to wait
            _restartQueueSignal.Reset();
            // On block processing, we cancel the block process cts, causing current task to get cancelled.
            _blockProcessorCancellationTokenSource.Cancel();
        }
    }

    private void BlockProcessorOnBlockProcessed(object? sender, BlockProcessedEventArgs e)
    {
        // Once block is processed, we replace the cancellation token with a fresh uncancelled one
        using CancellationTokenSource oldTokenSource = Interlocked.Exchange(
            ref _blockProcessorCancellationTokenSource,
            new CancellationTokenSource());

        // We also set queue signal causing it to continue processing task.
        _restartQueueSignal.Set();
    }

    private async Task StartChannel()
    {
        while (await _taskQueue.Reader.WaitToReadAsync(_mainCancellationTokenSource.Token))
        {
            // Create fresh CancellationTokenSource for current block processing
            CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(
                        _blockProcessorCancellationTokenSource.Token,
                        _mainCancellationTokenSource.Token);
            try
            {
                CancellationToken token = cts.Token;
                while (_taskQueue.Reader.TryRead(out IActivity activity))
                {
                    Interlocked.Decrement(ref _queueCount);
                    if (token.IsCancellationRequested)
                    {
                        // In case of task that is suppose to run when a block is being processed, if there is some time left
                        // from its deadline, we re-queue it. We do this in case there are some task in the queue that already
                        // reached deadline during block processing in which case, it will need to execute in order to handle
                        // its cancellation.
                        if (DateTimeOffset.UtcNow < activity.Deadline)
                        {
                            Interlocked.Increment(ref _queueCount);
                            await _taskQueue.Writer.WriteAsync(activity);
                            UpdateQueueCount();
                            // Requeued, throttle to prevent infinite loop.
                            // The tasks are in priority order, so we know next is same deadline or longer
                            // And we want to exit inner loop to refresh CancellationToken
                            goto Throttle;
                        }
                    }

                    UpdateQueueCount();
                    await activity.Do(token);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception e)
            {
                if (_logger.IsError) _logger.Error($"Error processing background task {e}.");
            }
            finally
            {
                cts.Dispose();
            }

            continue;

        Throttle:
            await Task.Delay(millisecondsDelay: 1);
        }
    }

    public void ScheduleTask<TReq>(TReq request, Func<TReq, CancellationToken, Task> fulfillFunc, TimeSpan? timeout = null)
    {
        timeout ??= DefaultTimeout;
        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout.Value;

        IActivity activity = new Activity<TReq>
        {
            Deadline = deadline,
            Request = request,
            FulfillFunc = fulfillFunc,
        };

        Evm.Metrics.IncrementTotalBackgroundTasksQueued();

        bool success = false;
        lock (_queueLock)
        {
            if (_queueCount + 1 < _capacity)
            {
                success = _taskQueue.Writer.TryWrite(activity);
                if (success)
                {
                    Interlocked.Increment(ref _queueCount);
                }
            }
        }

        if (success)
        {
            UpdateQueueCount();
        }
        else
        {
            request.TryDispose();
            // This should never happen unless something goes very wrong.
            UnableToWriteToTaskQueue();
        }

        [StackTraceHidden, DoesNotReturn]
        static void UnableToWriteToTaskQueue()
            => throw new InvalidOperationException("Unable to write to background task queue.");
    }

    private void UpdateQueueCount()
        => Evm.Metrics.NumberOfBackgroundTasksScheduled = Volatile.Read(ref _queueCount);

    public async ValueTask DisposeAsync()
    {
        _blockProcessor.BlocksProcessing -= BlockProcessorOnBlocksProcessing;
        _blockProcessor.BlockProcessed -= BlockProcessorOnBlockProcessed;

        _taskQueue.Writer.Complete();
        await _mainCancellationTokenSource.CancelAsync();
        await Task.WhenAll(_tasksExecutors);
        _mainCancellationTokenSource.Dispose();
        _scheduler.Dispose();
    }

    private readonly struct Activity<TReq> : IActivity
    {
        private static CancellationToken CancelledToken { get; } = CreateCancelledToken();

        private static CancellationToken CreateCancelledToken()
        {
            CancellationTokenSource cts = new();
            cts.Cancel();
            return cts.Token;
        }

        public DateTimeOffset Deadline { get; init; }
        public TReq Request { get; init; }
        public Func<TReq, CancellationToken, Task> FulfillFunc { get; init; }

        public int CompareTo(IActivity? other)
            => Deadline.CompareTo(other.Deadline);

        public async Task Do(CancellationToken cancellationToken)
        {
            TimeSpan timeToComplete = Deadline - DateTimeOffset.UtcNow;

            CancellationTokenSource? cts = null;
            CancellationToken token;
            if (timeToComplete <= TimeSpan.Zero)
            {
                // Cancel immediately. Got no time left.
                token = CancelledToken;
            }
            else
            {
                cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(timeToComplete);
                token = cts.Token;
            }

            try
            {
                await FulfillFunc.Invoke(Request, token);
            }
            finally
            {
                cts?.Dispose();
            }
        }
    }

    private interface IActivity : IComparable<IActivity>
    {
        DateTimeOffset Deadline { get; }
        Task Do(CancellationToken cancellationToken);
    }

    private sealed class BelowNormalPriorityTaskScheduler : TaskScheduler, IDisposable
    {
        private readonly BlockingCollection<Task> _tasks = [];
        private readonly Thread[] workerThreads;
        private readonly ManualResetEventSlim _restartQueueSignal;
        private readonly int _maxDegreeOfParallelism;
        private readonly ILogger _logger;
        private readonly CancellationToken _cancellationToken;

        public BelowNormalPriorityTaskScheduler(int maxDegreeOfParallelism, ManualResetEventSlim restartQueueSignal, ILogManager logManager, CancellationToken cancellationToken)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(maxDegreeOfParallelism, 1);

            _logger = logManager.GetClassLogger();
            _restartQueueSignal = restartQueueSignal;
            _maxDegreeOfParallelism = maxDegreeOfParallelism;
            _cancellationToken = cancellationToken;
            workerThreads = [.. Enumerable.Range(0, maxDegreeOfParallelism)
                            .Select(i =>
                            {
                                Thread thread = new (ProcessBackgroundTasks)
                                {
                                    IsBackground = true,
                                    Priority = ThreadPriority.BelowNormal,
                                    Name = $"Nethermind Background {i + 1}",
                                };
                                thread.Start();
                                return thread;
                            })];
        }

        private void ProcessBackgroundTasks(object _)
        {
            try
            {
                foreach (Task task in _tasks.GetConsumingEnumerable(_cancellationToken))
                {
                    // Wait if processing blocks
                    _restartQueueSignal.Wait(_cancellationToken);
                    try
                    {
                        TryExecuteTask(task);
                    }
                    catch (OperationCanceledException)
                    {
                    }
                    catch (Exception e)
                    {
                        if (_logger.IsError) _logger.Error($"Error processing background task {e}.");
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception e)
            {
                if (_logger.IsError) _logger.Error($"Error in background task processing {e}.");
            }
        }

        public override int MaximumConcurrencyLevel => _maxDegreeOfParallelism;
        protected override void QueueTask(Task task) => _tasks.Add(task);

        // Attempts to execute the task synchronously on the current thread.
        // We disallow inline execution to ensure our bound holds.
        protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued)
            => false;
        // For debugger support only
        protected override IEnumerable<Task> GetScheduledTasks() => _tasks.ToArray();

        public void Dispose()
        {
            if (_logger.IsInfo) _logger.Info("Disposing Background Scheduler");

            _tasks.CompleteAdding();
            foreach (Thread thread in workerThreads)
            {
                thread.Join();
            }
            _tasks.Dispose();
        }
    }
}
