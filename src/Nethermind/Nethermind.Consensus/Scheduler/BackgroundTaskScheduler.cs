// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
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
/// - During block processing, tasks run with a cancelled CancellationToken so handlers can bail out quickly.
///   If queue depth exceeds 80% capacity, tasks are disposed without execution to prevent queue overflow.
///   It is up to running tasks to check the token and stop promptly. Don't hang — other tasks need to be canceled too.
/// - A failure at this level is considered unexpected and loud. Exception should be handled at handler level.
/// </summary>
public class BackgroundTaskScheduler : IBackgroundTaskScheduler, IAsyncDisposable
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(2);

    private readonly CancellationTokenSource _mainCancellationTokenSource;
    private readonly Channel<IActivity> _taskQueue;
    private readonly BelowNormalPriorityTaskScheduler _scheduler;
    private readonly Task[] _tasksExecutors;
    private readonly ILogger _logger;
    private readonly IBranchProcessor _branchProcessor;
    private readonly IChainHeadInfoProvider _headInfo;
    private readonly int _capacity;
    private readonly long _highWatermark;
    private long _queueCount;

    private CancellationTokenSource _blockProcessorCancellationTokenSource;
    private long _lastDropLogTicks;
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
        _capacity = capacity;
        _highWatermark = (long)(capacity * 0.8);

        _branchProcessor.BlocksProcessing += BranchProcessorOnBranchesProcessing;
        _branchProcessor.BlockProcessed += BranchProcessorOnBranchProcessed;

        // TaskScheduler to run tasks at BelowNormal priority
        _scheduler = new BelowNormalPriorityTaskScheduler(
            concurrency,
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
            long depth = Volatile.Read(ref _queueCount);
            if (_logger.IsDebug) _logger.Debug($"Block processing starting, background queue depth: {depth}");
            // On block processing, cancel the block process CTS so running tasks can exit quickly
            _blockProcessorCancellationTokenSource.Cancel();
        }
    }

    private void BranchProcessorOnBranchProcessed(object? sender, BlockProcessedEventArgs e)
    {
        // Once the block is processed, we replace the cancellation token with a fresh uncanceled one
        using CancellationTokenSource oldTokenSource = Interlocked.Exchange(
            ref _blockProcessorCancellationTokenSource,
            new CancellationTokenSource());
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
                    UpdateQueueCount();
                    if (token.IsCancellationRequested && Volatile.Read(ref _queueCount) > _highWatermark)
                    {
                        // Queue pressure during block processing — skip work, just free resources.
                        // Avoids CTS allocation, handler invocation, and network I/O for each stale task.
                        activity.TryDispose();
                        Evm.Metrics.IncrementTotalBackgroundTasksCancelled();
                    }
                    else
                    {
                        await activity.Do(token);
                        Evm.Metrics.IncrementTotalBackgroundTasksExecuted();
                    }
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
        }
    }

    public bool TryScheduleTask<TReq>(TReq request, Func<TReq, CancellationToken, Task> fulfillFunc, TimeSpan? timeout = null, string? source = null)
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
                return true;
            }
        }

        Evm.Metrics.IncrementTotalBackgroundTasksDropped();
        long now = Environment.TickCount64;
        long lastLog = Volatile.Read(ref _lastDropLogTicks);
        if (_logger.IsWarn && now - lastLog > 10_000 && Interlocked.CompareExchange(ref _lastDropLogTicks, now, lastLog) == lastLog)
        {
            _logger.Warn(
                $"Background task queue is full (Count: {_queueCount}, Capacity: {_capacity}), dropping task [{source ?? "unknown"}]. " +
                $"Totals: queued={Evm.Metrics.TotalBackgroundTasksQueued}, executed={Evm.Metrics.TotalBackgroundTasksExecuted}, " +
                $"cancelled={Evm.Metrics.TotalBackgroundTasksCancelled}, dropped={Evm.Metrics.TotalBackgroundTasksDropped}");
        }
        Interlocked.Decrement(ref _queueCount);
        DisposeDeep(request);
        return false;
    }

    /// <summary>
    /// Recursively disposes IDisposable members buried inside ValueTuples.
    /// Requests are often wrapped as ((actualRequest, func), outerFunc) by BackgroundTaskSchedulerWrapper,
    /// so a plain TryDispose() on the outer tuple is a no-op — the inner IOwnedReadOnlyList etc. would leak.
    /// </summary>
    private static void DisposeDeep<T>(T value)
    {
        if (value is IDisposable disposable)
        {
            disposable.Dispose();
        }
        else if (value is ITuple tuple)
        {
            for (int i = 0; i < tuple.Length; i++)
            {
                object? element = tuple[i];
                if (element is IDisposable d)
                    d.Dispose();
                else if (element is ITuple nested)
                {
                    for (int j = 0; j < nested.Length; j++)
                        (nested[j] as IDisposable)?.Dispose();
                }
            }
        }
    }

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

    private readonly struct Activity<TReq> : IActivity
    {
        public DateTimeOffset Deadline { get; init; }
        public TReq Request { get; init; }
        public Func<TReq, CancellationToken, Task> FulfillFunc { get; init; }

        public int CompareTo(IActivity? other) => Deadline.CompareTo(other?.Deadline ?? DateTimeOffset.MaxValue);

        public void TryDispose() => DisposeDeep(Request);

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
    }

    private interface IActivity : IComparable<IActivity>
    {
        DateTimeOffset Deadline { get; }
        Task Do(CancellationToken cancellationToken);
        void TryDispose();
    }

    private sealed class BelowNormalPriorityTaskScheduler : TaskScheduler, IDisposable
    {
        private readonly BlockingCollection<Task> _tasks = [];
        private readonly Thread[] workerThreads;
        private readonly int _maxDegreeOfParallelism;
        private readonly ILogger _logger;
        private readonly CancellationToken _cancellationToken;

        public BelowNormalPriorityTaskScheduler(int maxDegreeOfParallelism, ILogManager logManager, CancellationToken cancellationToken)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(maxDegreeOfParallelism, 1);

            _logger = logManager.GetClassLogger();
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
