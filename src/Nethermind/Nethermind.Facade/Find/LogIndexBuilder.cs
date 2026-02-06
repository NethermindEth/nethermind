// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Db.LogIndex;
using Nethermind.Logging;
using Timer = System.Timers.Timer;
using static System.Threading.Tasks.TaskCreationOptions;

namespace Nethermind.Facade.Find;

// TODO: reduce periodic logging
public sealed class LogIndexBuilder : ILogIndexBuilder
{
    private sealed class ProcessingQueue
    {
        private readonly TransformBlock<IReadOnlyList<BlockReceipts>, LogIndexAggregate> _aggregateBlock;
        private readonly ActionBlock<LogIndexAggregate> _addReceiptsBlock;

        public int QueueCount => _aggregateBlock.InputCount + _addReceiptsBlock.InputCount;
        public Task WriteAsync(IReadOnlyList<BlockReceipts> batch, CancellationToken cancellation) => _aggregateBlock.SendAsync(batch, cancellation);
        public Task Completion => Task.WhenAll(_aggregateBlock.Completion, _addReceiptsBlock.Completion);

        public ProcessingQueue(
            TransformBlock<IReadOnlyList<BlockReceipts>, LogIndexAggregate> aggregateBlock,
            ActionBlock<LogIndexAggregate> addReceiptsBlock)
        {
            _aggregateBlock = aggregateBlock;
            _addReceiptsBlock = addReceiptsBlock;
        }
    }

    private readonly IBlockTree _blockTree;
    private readonly ISyncConfig _syncConfig;
    private readonly ILogger _logger;

    private readonly CancellationTokenSource _cancellationSource = new();
    private CancellationToken CancellationToken => _cancellationSource.Token;

    private int MaxReorgDepth => _config.MaxReorgDepth;
    private static readonly TimeSpan NewBlockWaitTimeout = TimeSpan.FromSeconds(5);

    private readonly ILogIndexStorage _logIndexStorage;
    private readonly ILogIndexConfig _config;
    private readonly IReceiptStorage _receiptStorage;
    private readonly ILogManager _logManager;
    private Timer? _progressLoggerTimer;

    private readonly TaskCompletionSource<int> _pivotSource = new(RunContinuationsAsynchronously);
    private readonly Task<int> _pivotTask;

    private readonly List<Task> _tasks = new();

    private readonly Dictionary<bool, ProgressLogger> _progressLoggers = new();
    private readonly Dictionary<bool, ProcessingQueue> _processingQueues = new();
    private readonly Dictionary<bool, TaskCompletionSource> _completions = new()
    {
        [false] = new(RunContinuationsAsynchronously),
        [true] = new(RunContinuationsAsynchronously),
    };

    private LogIndexUpdateStats _stats;

    public string Description => "log index builder";

    public Task BackwardSyncCompletion => _completions[false].Task;

    public LogIndexBuilder(ILogIndexStorage logIndexStorage, ILogIndexConfig config,
        IBlockTree blockTree, ISyncConfig syncConfig, IReceiptStorage receiptStorage,
        ILogManager logManager)
    {
        ArgumentNullException.ThrowIfNull(logIndexStorage);
        ArgumentNullException.ThrowIfNull(blockTree);
        ArgumentNullException.ThrowIfNull(receiptStorage);
        ArgumentNullException.ThrowIfNull(logManager);
        ArgumentNullException.ThrowIfNull(syncConfig);

        _config = config;
        _logIndexStorage = logIndexStorage;
        _blockTree = blockTree;
        _syncConfig = syncConfig;
        _receiptStorage = receiptStorage;
        _logManager = logManager;
        _logger = logManager.GetClassLogger<LogIndexBuilder>();
        _pivotTask = _pivotSource.Task;
        _stats = new(_logIndexStorage);
    }

    private void StartProcessing(bool isForward)
    {
        // Do not start backward sync if the target is already reached
        if (!isForward && _logIndexStorage.MinBlockNumber <= MinTargetBlockNumber)
        {
            MarkCompleted(false);
            return;
        }

        _processingQueues[isForward] = BuildQueue(isForward);
        _progressLoggers[isForward] = new(GetLogPrefix(isForward), _logManager);

        _tasks.AddRange(
            Task.Run(() => DoQueueBlocks(isForward), CancellationToken),
            _processingQueues[isForward].Completion
        );
    }

    public async Task StartAsync()
    {
        try
        {
            if (!_config.Enabled)
                return;

            _receiptStorage.ReceiptsInserted += OnReceiptsInserted;

            TrySetPivot(_logIndexStorage.MaxBlockNumber);
            TrySetPivot((int)_blockTree.SyncPivot.BlockNumber);

            if (!_pivotTask.IsCompleted && _logger.IsInfo)
                _logger.Info($"{GetLogPrefix()}: waiting for the first block...");

            await _pivotTask;

            StartProcessing(isForward: true);
            StartProcessing(isForward: false);

            UpdateProgress();
            LogProgress();

            _progressLoggerTimer = new(TimeSpan.FromSeconds(30));
            _progressLoggerTimer.AutoReset = true;
            _progressLoggerTimer.Elapsed += (_, _) => LogProgress();
            _progressLoggerTimer.Start();

            IsRunning = true;
        }
        catch (Exception ex)
        {
            await HandleExceptionAsync(ex);
        }
    }

    public async Task StopAsync()
    {
        if (!_config.Enabled)
            return;

        await _cancellationSource.CancelAsync();

        _pivotSource.TrySetCanceled(CancellationToken);
        _progressLoggerTimer?.Stop();

        foreach (Task task in _tasks)
        {
            try
            {
                await task;
            }
            catch (Exception ex)
            {
                await HandleExceptionAsync(ex, isStopping: true);
            }
        }

        await _logIndexStorage.StopAsync();

        IsRunning = false;
    }

    public async ValueTask DisposeAsync()
    {
        await _logIndexStorage.DisposeAsync();
        _progressLoggerTimer?.Dispose();
        _cancellationSource.Dispose();
    }

    private void LogStats()
    {
        LogIndexUpdateStats stats = _stats;

        if (stats is not { BlocksAdded: > 0 })
            return;

        _stats = new(_logIndexStorage);

        if (_logger.IsInfo)
        {
            _logger.Info(_config.DetailedLogs
                    ? $"{GetLogPrefix()}: {stats:d}"
                    : $"{GetLogPrefix()}: {stats}"
            );
        }
    }

    private void LogProgress()
    {
        LogStats();
        foreach ((_, ProgressLogger progress) in _progressLoggers)
            progress.LogProgress();
    }

    private bool TrySetPivot(int? blockNumber)
    {
        if (blockNumber is not { } number || number is 0)
            return false;

        if (_pivotSource.Task.IsCompleted)
            return false;

        number = Math.Max(MinTargetBlockNumber, number);
        number = Math.Min(MaxTargetBlockNumber, number);

        if (number is 0)
            return false;

        if (!TryGetBlockReceipts(number, out _))
            return false;

        if (!_pivotSource.TrySetResult(number))
            return false;

        _logger.Info($"{GetLogPrefix()}: using block {number} as pivot.");
        return true;
    }

    private void OnReceiptsInserted(object? sender, ReceiptsEventArgs args)
    {
        var next = (int)args.BlockHeader.Number;
        if (TrySetPivot(next))
            _receiptStorage.ReceiptsInserted -= OnReceiptsInserted;
    }

    public int MaxTargetBlockNumber => (int)Math.Max(_blockTree.BestKnownNumber - MaxReorgDepth, 0);

    // Block 0 should always be present
    public int MinTargetBlockNumber => (int)(_syncConfig.AncientReceiptsBarrierCalc <= 1 ? 0 : _syncConfig.AncientReceiptsBarrierCalc);

    public bool IsRunning { get; private set; }
    public DateTimeOffset? LastUpdate { get; private set; }
    public Exception? LastError { get; private set; }

    private ProcessingQueue BuildQueue(bool isForward)
    {
        var aggregateBlock = new TransformBlock<IReadOnlyList<BlockReceipts>, LogIndexAggregate>(
            batch => Aggregate(batch, isForward),
            new()
            {
                BoundedCapacity = _config.MaxAggregationQueueSize,
                MaxDegreeOfParallelism = _config.MaxAggregationParallelism,
                CancellationToken = CancellationToken,
                SingleProducerConstrained = true
            }
        );

        var addReceiptsBlock = new ActionBlock<LogIndexAggregate>(
            aggr => AddReceiptsAsync(aggr, isForward),
            new()
            {
                BoundedCapacity = _config.MaxSavingQueueSize,
                MaxDegreeOfParallelism = 1,
                CancellationToken = CancellationToken,
                SingleProducerConstrained = true
            }
        );

        aggregateBlock.Completion.ContinueWith(t => HandleExceptionAsync(t.Exception), TaskContinuationOptions.OnlyOnFaulted);
        addReceiptsBlock.Completion.ContinueWith(t => HandleExceptionAsync(t.Exception), TaskContinuationOptions.OnlyOnFaulted);

        aggregateBlock.LinkTo(addReceiptsBlock, new() { PropagateCompletion = true });
        return new(aggregateBlock, addReceiptsBlock);
    }

    private async Task HandleExceptionAsync(Exception? exception, bool isStopping = false)
    {
        if (exception is null)
            return;

        if (exception is AggregateException a)
            exception = a.InnerException;

        if (exception is OperationCanceledException oc && oc.CancellationToken == CancellationToken)
            return; // Cancelled

        if (_logger.IsError)
            _logger.Error($"{GetLogPrefix()} failed. Please restart the client.", exception);

        LastError = exception;

        foreach (var isForward in _completions.Keys)
            _completions[isForward].TrySetException(exception!);

        if (!isStopping)
            await StopAsync();
    }

    private LogIndexAggregate Aggregate(IReadOnlyList<BlockReceipts> batch, bool isForward)
    {
        return _logIndexStorage.Aggregate(batch, !isForward, _stats);
    }

    private async Task AddReceiptsAsync(LogIndexAggregate aggregate, bool isForward)
    {
        if (GetNextBlockNumber(_logIndexStorage, isForward) is { } next && next != aggregate.FirstBlockNum)
            throw new($"{GetLogPrefix(isForward)}: non sequential batches: ({aggregate.FirstBlockNum} instead of {next}).");

        await _logIndexStorage.AddReceiptsAsync(aggregate, _stats);
        LastUpdate = DateTimeOffset.Now;

        UpdateProgress();

        if (_logIndexStorage.MinBlockNumber <= MinTargetBlockNumber)
            MarkCompleted(false);
    }

    private async Task DoQueueBlocks(bool isForward)
    {
        try
        {
            var pivotNumber = await _pivotTask;

            ProcessingQueue queue = _processingQueues![isForward];

            var next = GetNextBlockNumber(isForward);
            if (next is not { } start)
            {
                if (isForward)
                {
                    start = pivotNumber;
                }
                else
                {
                    start = pivotNumber - 1;
                }
            }

            var buffer = new BlockReceipts[_config.MaxBatchSize];
            while (!CancellationToken.IsCancellationRequested)
            {
                if (!isForward && start < MinTargetBlockNumber)
                {
                    if (_logger.IsTrace)
                        _logger.Trace($"{GetLogPrefix(isForward)}: queued last block");

                    return;
                }

                var batchSize = _config.MaxBatchSize;
                var end = isForward ? start + batchSize - 1 : start - batchSize + 1;
                end = Math.Max(end, MinTargetBlockNumber);
                end = Math.Min(end, MaxTargetBlockNumber);

                // from - inclusive, to - exclusive
                var (from, to) = isForward
                    ? (start, end + 1)
                    : (end, start + 1);

                var timestamp = Stopwatch.GetTimestamp();
                Array.Clear(buffer);
                ReadOnlySpan<BlockReceipts> batch = GetNextBatch(from, to, buffer, isForward, CancellationToken);

                if (batch.Length == 0)
                {
                    // TODO: stop waiting immediately when receipts become available
                    await Task.Delay(NewBlockWaitTimeout, CancellationToken);
                    continue;
                }

                _stats.LoadingReceipts.Include(Stopwatch.GetElapsedTime(timestamp));

                start = GetNextBlockNumber(batch[^1].BlockNumber, isForward);
                await queue.WriteAsync(batch.ToArray(), CancellationToken);
            }
        }
        catch (Exception ex)
        {
            await HandleExceptionAsync(ex);
        }

        if (_logger.IsTrace)
            _logger.Trace($"{GetLogPrefix(isForward)}: queueing completed.");
    }

    private void UpdateProgress()
    {
        if (!_pivotTask.IsCompletedSuccessfully) return;
        var pivotNumber = _pivotTask.Result;

        if (_progressLoggers.TryGetValue(true, out ProgressLogger forwardProgress) && !forwardProgress.HasEnded)
        {
            forwardProgress.TargetValue = Math.Max(0, _blockTree.BestKnownNumber - MaxReorgDepth - pivotNumber + 1);
            forwardProgress.Update(_logIndexStorage.MaxBlockNumber is { } max ? max - pivotNumber + 1 : 0);
            forwardProgress.CurrentQueued = _processingQueues[true].QueueCount;
        }

        if (_progressLoggers.TryGetValue(false, out ProgressLogger backwardProgress) && !backwardProgress.HasEnded)
        {
            backwardProgress.TargetValue = pivotNumber - MinTargetBlockNumber;
            backwardProgress.Update(_logIndexStorage.MinBlockNumber is { } min ? pivotNumber - min : 0);
            backwardProgress.CurrentQueued = _processingQueues[false].QueueCount;
        }
    }

    private void MarkCompleted(bool isForward)
    {
        if (!_completions[isForward].TrySetResult())
            return;

        if (_progressLoggers.TryGetValue(isForward, out ProgressLogger progress))
            progress.MarkEnd();

        if (_logger.IsInfo)
            _logger.Info($"{GetLogPrefix(isForward)}: completed.");
    }

    private static int? GetNextBlockNumber(ILogIndexStorage storage, bool isForward)
    {
        return isForward ? storage.MaxBlockNumber + 1 : storage.MinBlockNumber - 1;
    }

    private int? GetNextBlockNumber(bool isForward) => GetNextBlockNumber(_logIndexStorage, isForward);

    private static int GetNextBlockNumber(int last, bool isForward)
    {
        return isForward ? last + 1 : last - 1;
    }

    private ReadOnlySpan<BlockReceipts> GetNextBatch(int from, int to, BlockReceipts[] buffer, bool isForward, CancellationToken token)
    {
        if (to <= from)
            return ReadOnlySpan<BlockReceipts>.Empty;

        if (to - from > buffer.Length)
            throw new InvalidOperationException($"{GetLogPrefix()}: buffer size is too small: {buffer.Length} / {to - from}");

        // Check the immediate next block first
        var nextIndex = isForward ? from : to - 1;
        if (!TryGetBlockReceipts(nextIndex, out buffer[0]))
            return ReadOnlySpan<BlockReceipts>.Empty;

        Parallel.For(from, to, new()
        {
            CancellationToken = token,
            MaxDegreeOfParallelism = _config.MaxReceiptsParallelism
        }, i =>
        {
            var bufferIndex = isForward ? i - from : to - 1 - i;
            if (buffer[bufferIndex] == default)
                TryGetBlockReceipts(i, out buffer[bufferIndex]);
        });

        var endIndex = Array.IndexOf(buffer, default);
        return endIndex < 0 ? buffer : buffer.AsSpan(..endIndex);
    }

    // TODO: move to IReceiptStorage?
    private bool TryGetBlockReceipts(int i, out BlockReceipts blockReceipts)
    {
        blockReceipts = default;

        if (_blockTree.FindBlock(i, BlockTreeLookupOptions.ExcludeTxHashes) is not { Hash: not null } block)
        {
            return false;
        }

        if (!block.Header.HasTransactions)
        {
            blockReceipts = new(i, []);
            return true;
        }

        TxReceipt[] receipts = _receiptStorage.Get(block) ?? [];

        if (receipts.Length == 0)
        {
            return false; // block should have transactions but nothing in storage
        }

        blockReceipts = new(i, receipts);
        return true;
    }

    private static string GetLogPrefix(bool? isForward = null) => isForward switch
    {
        true => "Log index sync (Forward)",
        false => "Log index sync (Backward)",
        _ => "Log index sync"
    };
}
