// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
/// Provides a way to orchestrate tasks to run in the background at a lower priority.
/// - Task will be run in a lower priority thread, but there is a concurrency limit.
/// - Task closure will have the CancellationToken which will be canceled if block processing happens while the task is running.
/// - The Task has a default timeout, which is counted from the time it is queued. If timed out because too many other background
///    tasks before it, for example, the cancellation token passed to it will be canceled.
/// - Task will still run when block processing is happening, and it's timed out this is so that it can handle its cancellation.
/// - Task will not run if block processing is happening, and it still has some time left.
///   It is up to the task to determine what happens if canceled, maybe it will reschedule for later or resume later, but
///   preferably, stop execution immediately. Don't hang BTW. Other background tasks need to be canceled too.
/// - A failure at this level is considered unexpected and loud. Exception should be handled at handler level.
/// </summary>
public class BackgroundTaskScheduler : IBackgroundTaskScheduler, IAsyncDisposable
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(2);

    private readonly CancellationTokenSource _mainCancellationTokenSource;
    private readonly Channel<IActivity> _taskQueue;
    private readonly BelowNormalPriorityTaskScheduler _scheduler;
    private readonly ManualResetEventSlim _restartQueueSignal;
    private readonly Task[] _tasksExecutors;
    private readonly ILogger _logger;
    private readonly IBranchProcessor _branchProcessor;
    private readonly IChainHeadInfoProvider _headInfo;
    private readonly int _capacity;
    private long _queueCount;
    private readonly ConcurrentDictionary<string, int> _stats = new();

    private CancellationTokenSource _blockProcessorCancellationTokenSource;
    private bool _disposed = false;

    public BackgroundTaskScheduler(IBranchProcessor branchProcessor, IChainHeadInfoProvider headInfo, int concurrency, int capacity, ILogManager logManager)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(concurrency, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);

        _mainCancellationTokenSource = new CancellationTokenSource();
        _blockProcessorCancellationTokenSource = new CancellationTokenSource();

        // In priority order, so if we reach an activity with time left,
        // we know the rest still have time left
        _taskQueue = Channel.CreateUnboundedPrioritized(
            new UnboundedPrioritizedChannelOptions<IActivity>
            {
                SingleReader = concurrency == 1,
                SingleWriter = false,
                AllowSynchronousContinuations = false
            });
        _logger = logManager.GetClassLogger();
        _branchProcessor = branchProcessor;
        _headInfo = headInfo;
        _restartQueueSignal = new ManualResetEventSlim(initialState: true);
        _capacity = capacity;

        _branchProcessor.BlocksProcessing += BranchProcessorOnBranchesProcessing;
        _branchProcessor.BlockProcessed += BranchProcessorOnBranchProcessed;

        // TaskScheduler to run tasks at BelowNormal priority
        _scheduler = new BelowNormalPriorityTaskScheduler(
            concurrency,
            _restartQueueSignal,
            logManager,
            _mainCancellationTokenSource.Token);

        TaskFactory factory = new(_scheduler);
        _tasksExecutors = [.. Enumerable.Range(0, concurrency).Select(_ => factory.StartNew(StartChannel).Unwrap())];
    }

    private void BranchProcessorOnBranchesProcessing(object? sender, BlocksProcessingEventArgs e)
    {
        // If we are syncing, we don't block background task processing
        // as there are potentially no gaps between blocks
        if (!_headInfo.IsSyncing)
        {
            // Reset the background queue processing signal, causing it to wait
            _restartQueueSignal.Reset();
            // On block processing, we cancel the block process cts, causing the current task to get canceled.
            _blockProcessorCancellationTokenSource.Cancel();
        }
    }

    private void BranchProcessorOnBranchProcessed(object? sender, BlockProcessedEventArgs e)
    {
        // Once the block is processed, we replace the cancellation token with a fresh uncanceled one
        using CancellationTokenSource oldTokenSource = Interlocked.Exchange(
            ref _blockProcessorCancellationTokenSource,
            new CancellationTokenSource());

        // We also set a queue signal causing it to continue processing the task.
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
                        // In case of a task supposed to run when a block is being processed, if there is some time left
                        // from its deadline, we re-queue it. We do this in case there is some task in the queue that already
                        // reached deadline during block processing, in which case, it will need to execute to handle
                        // its cancellation.
                        if (DateTimeOffset.UtcNow < activity.Deadline)
                        {
                            Interlocked.Increment(ref _queueCount);
                            await _taskQueue.Writer.WriteAsync(activity);
                            UpdateQueueCount();
                            // Re-queued, throttle to prevent infinite loop.
                            // The tasks are in priority order, so we know next is the same deadline or longer,
                            // And we want to exit the inner loop to refresh CancellationToken
                            goto Throttle;
                        }
                    }

                    UpdateQueueCount();
                    DecrementStats(activity);
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

    public bool TryScheduleTask<TReq>(in TReq request, Func<TReq, CancellationToken, Task> fulfillFunc, TimeSpan? timeout = null) where TReq : notnull
    {
        IActivity activity = new Activity<TReq>
        {
            Deadline = DateTimeOffset.UtcNow + (timeout ?? DefaultTimeout),
            Request = request,
            FulfillFunc = fulfillFunc,
        };

        Evm.Metrics.IncrementTotalBackgroundTasksQueued();

        if (Interlocked.Increment(ref _queueCount) <= _capacity)
        {
            if (_taskQueue.Writer.TryWrite(activity))
            {
                UpdateQueueCount();
                IncrementStats(request);
                return true;
            }
        }

        long queueCount = Interlocked.Decrement(ref _queueCount);
        request.TryDispose();
        if (_logger.IsWarn) _logger.Warn($"Background task queue is full (Count: {queueCount}, Capacity: {_capacity}), dropping task. " +
                                         $"Stats: {string.Join(", ", _stats.Where(kv => kv.Value > 0).Select(kv => $"({kv.Key}: {kv.Value})"))}");
        return false;
    }

    private void IncrementStats<TReq>(TReq request) where TReq : notnull =>
        _stats.AddOrUpdate(request.ToString(), 1, (_, value) => value + 1);

    private void DecrementStats(IActivity activity) =>
        _stats.AddOrUpdate(activity.ToString(), 0, (_, value) => value - 1);

    private void UpdateQueueCount() => Evm.Metrics.NumberOfBackgroundTasksScheduled = Volatile.Read(ref _queueCount);

    public async ValueTask DisposeAsync()
    {
        if (!Interlocked.CompareExchange(ref _disposed, true, false)) return;

        _branchProcessor.BlocksProcessing -= BranchProcessorOnBranchesProcessing;
        _branchProcessor.BlockProcessed -= BranchProcessorOnBranchProcessed;

        _taskQueue.Writer.Complete();
        await _mainCancellationTokenSource.CancelAsync();
        await Task.WhenAll(_tasksExecutors);
        _mainCancellationTokenSource.Dispose();
        _scheduler.Dispose();
    }

    public IReadOnlyDictionary<string, int> GetStats() => _stats;

    private readonly struct Activity<TReq> : IActivity where TReq : notnull
    {
        public DateTimeOffset Deadline { get; init; }
        public TReq Request { get; init; }
        public Func<TReq, CancellationToken, Task> FulfillFunc { get; init; }

        public int CompareTo(IActivity? other) => Deadline.CompareTo(other?.Deadline ?? DateTimeOffset.MaxValue);

        public async Task Do(CancellationToken cancellationToken)
        {
            TimeSpan timeToComplete = Deadline - DateTimeOffset.UtcNow;

            CancellationTokenSource? cts = null;
            CancellationToken token;
            if (timeToComplete <= TimeSpan.Zero)
            {
                // Cancel immediately. Got no time left.
                token = CancellationTokenExtensions.AlreadyCancelledToken;
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

        public override string ToString() => Request.ToString();
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
