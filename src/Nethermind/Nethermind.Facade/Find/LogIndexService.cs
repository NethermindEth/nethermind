// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Db;
using Nethermind.Logging;
using Timer = System.Timers.Timer;

namespace Nethermind.Facade.Find;

// TODO: move to correct namespace
// TODO: reduce periodic logging
public sealed class LogIndexService : ILogIndexService
{
    private sealed class ProcessingQueue
    {
        private readonly TransformBlock<IReadOnlyList<BlockReceipts>, LogIndexAggregate> _aggregateBlock;
        private readonly ActionBlock<LogIndexAggregate> _setReceiptsBlock;

        public int QueueCount => _aggregateBlock.InputCount + _setReceiptsBlock.InputCount;
        public Task WriteAsync(IReadOnlyList<BlockReceipts> batch, CancellationToken cancellation) => _aggregateBlock.SendAsync(batch, cancellation);
        public Task Completion => Task.WhenAll(_aggregateBlock.Completion, _setReceiptsBlock.Completion);

        public ProcessingQueue(
            TransformBlock<IReadOnlyList<BlockReceipts>, LogIndexAggregate> aggregateBlock,
            ActionBlock<LogIndexAggregate> setReceiptsBlock)
        {
            _aggregateBlock = aggregateBlock;
            _setReceiptsBlock = setReceiptsBlock;
        }
    }

    private readonly IBlockTree _blockTree;
    private readonly ISyncConfig _syncConfig;
    private readonly ILogger _logger;

    private readonly CancellationTokenSource _cancellationSource = new();
    private CancellationToken CancellationToken => _cancellationSource.Token;

    // TODO: take some/all values from chain config
    private const int MaxReorgDepth = 8;
    private const int BatchSize = 256;
    private const int MaxBatchQueueSize = 4096;
    private const int MaxAggregateQueueSize = 512;
    private static readonly int IOParallelism = 16;
    private static readonly int AggregateParallelism = Math.Max(Environment.ProcessorCount / 2, 1);
    private static readonly TimeSpan NewBlockWaitTimeout = TimeSpan.FromSeconds(5);

    private readonly ILogIndexStorage _logIndexStorage;
    private readonly IReceiptFinder _receiptFinder;
    private readonly IReceiptStorage _receiptStorage;
    private readonly ProgressLogger _forwardProgressLogger;
    private readonly ProgressLogger _backwardProgressLogger;
    private Timer? _progressLoggerTimer;

    // TODO: handle risk or potential reorg loss on restart
    private readonly Channel<BlockReceipts> _reorgChannel = Channel.CreateUnbounded<BlockReceipts>();

    private readonly TaskCompletionSource<int> _pivotSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task<int> _pivotTask;

    [SuppressMessage("ReSharper", "CollectionNeverQueried.Local")]
    private readonly CompositeDisposable _disposables = new();
    private readonly List<Task> _tasks = new();

    private Dictionary<bool, ProcessingQueue>? _processingQueues;
    private ConcurrentDictionary<bool, LogIndexUpdateStats>? _stats;

    public string Description => "log index service";

    public LogIndexService(ILogIndexStorage logIndexStorage, IBlockTree blockTree, ISyncConfig syncConfig,
        IReceiptFinder receiptFinder, IReceiptStorage receiptStorage, ILogManager logManager)
    {
        ArgumentNullException.ThrowIfNull(logIndexStorage);
        ArgumentNullException.ThrowIfNull(blockTree);
        ArgumentNullException.ThrowIfNull(receiptFinder);
        ArgumentNullException.ThrowIfNull(receiptStorage);
        ArgumentNullException.ThrowIfNull(logManager);
        ArgumentNullException.ThrowIfNull(syncConfig);

        _logIndexStorage = logIndexStorage;
        _blockTree = blockTree;
        _syncConfig = syncConfig;
        _receiptFinder = receiptFinder;
        _receiptStorage = receiptStorage;
        _logger = logManager.GetClassLogger<LogIndexService>();

        _forwardProgressLogger = new(GetLogPrefix(isForward: true), logManager);
        _backwardProgressLogger = new(GetLogPrefix(isForward: false), logManager);

        _pivotTask = _pivotSource.Task;
        if (_logIndexStorage.GetMaxBlockNumber() is { } maxNumber)
            _pivotSource.TrySetResult(maxNumber);
    }

    public async Task StartAsync()
    {
        try
        {
            _receiptStorage.AnyReceiptsInserted += OnReceiptsInserted;

            if (!_pivotTask.IsCompleted && _logger.IsInfo)
                _logger.Info($"{GetLogPrefix()}: waiting for the first block...");

            await _pivotTask;

            _stats = new()
            {
                [true] = new(_logIndexStorage),
                [false] = new(_logIndexStorage)
            };

            _processingQueues = new()
            {
                [true] = BuildQueue(isForward: true),
                [false] = BuildQueue(isForward: false)
            };

            _tasks.AddRange(
                Task.Run(() => DoQueueBlocks(isForward: true), CancellationToken),
                Task.Run(() => DoQueueBlocks(isForward: false), CancellationToken), // TODO: don't start if old receipts download is disabled
                _processingQueues[true].Completion,
                _processingQueues[false].Completion
            );

            UpdateProgress();
            LogProgress();

            _disposables.Add(_progressLoggerTimer = new(TimeSpan.FromSeconds(30)));
            _progressLoggerTimer.AutoReset = true;
            _progressLoggerTimer.Elapsed += (_, _) => LogProgress();
            _progressLoggerTimer.Start();
        }
        catch (Exception ex)
        {
            HandleException(ex);
        }
    }

    public async Task StopAsync()
    {
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
                HandleException(ex);
            }
        }

        await _logIndexStorage.StopAsync();
    }

    public async ValueTask DisposeAsync()
    {
        _disposables.Dispose();
        await _logIndexStorage.DisposeAsync();
    }

    private void LogStats(bool isForward)
    {
        LogIndexUpdateStats stats = _stats?[isForward];

        if (stats is not { BlocksAdded: > 0 })
            return;

        _stats[isForward] = new(_logIndexStorage);

        if (_logger.IsInfo) // TODO: log at debug/trace
            _logger.Info($"{GetLogPrefix(isForward)}: {stats:d}");
    }

    private void LogProgress()
    {
        _forwardProgressLogger.LogProgress(false);
        LogStats(isForward: true);

        _backwardProgressLogger.LogProgress(false);
        LogStats(isForward: false);
    }

    // TODO: add receipts to cache
    private void OnReceiptsInserted(object? sender, ReceiptsEventArgs args)
    {
        // if (args.WasRemoved)
        // {
        //     _reorgChannel.Writer.TryWrite(new((int)args.BlockHeader.Number, args.TxReceipts));
        //     return;
        // }

        //_logger.Info($"[TRACE] {nameof(OnReceiptsInserted)}: {args.BlockHeader.ToString(BlockHeader.Format.FullHashAndNumber)} [{args.TxReceipts.Length}]");

        var next = (int)args.BlockHeader.Number;

        if (next != 0 && !_pivotTask.IsCompleted && _pivotSource.TrySetResult(next) && _logger.IsInfo)
            _logger.Info($"{GetLogPrefix()}: using block {next} as pivot.");

        // var (min, max) = (_logIndexStorage.GetMinBlockNumber(), _logIndexStorage.GetMaxBlockNumber());
        //
        // if (min is null || next < min)
        //     _newBackwardBlockEvent.Set();
        //
        // if (max is null || next > max)
        //     _newForwardBlockEvent.Set();
    }

    public int GetMaxTargetBlockNumber()
    {
        return (int)Math.Max(_blockTree.BestKnownNumber - MaxReorgDepth, 0);
    }

    public int GetMinTargetBlockNumber()
    {
        // Block 0 should always be present
        return (int)(_syncConfig.AncientReceiptsBarrierCalc <= 1 ? 0 : _syncConfig.AncientReceiptsBarrierCalc);
    }

    private ProcessingQueue BuildQueue(bool isForward)
    {
        var aggregateBlock = new TransformBlock<IReadOnlyList<BlockReceipts>, LogIndexAggregate>(
            batch => Aggregate(batch, isForward),
            new() {
                BoundedCapacity = MaxBatchQueueSize / BatchSize, MaxDegreeOfParallelism = AggregateParallelism,
                CancellationToken = CancellationToken, SingleProducerConstrained = true
            }
        );

        var setReceiptsBlock = new ActionBlock<LogIndexAggregate>(
            aggr => SetReceiptsAsync(aggr, isForward),
            new()
            {
                BoundedCapacity = MaxAggregateQueueSize, MaxDegreeOfParallelism = 1,
                CancellationToken = CancellationToken, SingleProducerConstrained = true
            }
        );

        aggregateBlock.Completion.ContinueWith(t => HandleException(t.Exception), TaskContinuationOptions.OnlyOnFaulted);
        setReceiptsBlock.Completion.ContinueWith(t => HandleException(t.Exception), TaskContinuationOptions.OnlyOnFaulted);

        aggregateBlock.LinkTo(setReceiptsBlock, new() { PropagateCompletion = true });
        return new(aggregateBlock, setReceiptsBlock);
    }

    private void HandleException(Exception? exception)
    {
        if (exception is null)
            return;

        if (exception is OperationCanceledException oc && oc.CancellationToken == CancellationToken)
            return; // Cancelled

        if (exception is AggregateException a)
            exception = a.InnerException;

        if (_logger.IsError)
            _logger.Error($"{GetLogPrefix()} syncing failed. Please restart the client.", exception);

        _cancellationSource.Cancel();
    }

    private LogIndexAggregate Aggregate(IReadOnlyList<BlockReceipts> batch, bool isForward)
    {
        // TODO: remove ordering check to save time?
        if ((isForward && !IsSeqAsc(batch)) || (!isForward && !IsSeqDesc(batch)))
            throw new($"{GetLogPrefix(isForward)}: non-ordered batch in queue: ({batch[0]} -> {batch[^1]}).");

        return _logIndexStorage.Aggregate(batch, !isForward, _stats?[isForward]);
    }

    private async Task SetReceiptsAsync(LogIndexAggregate aggregate, bool isForward)
    {
        if (GetNextBlockNumber(_logIndexStorage, isForward) is { } next && next != aggregate.FirstBlockNum)
            throw new($"{GetLogPrefix(isForward)}: non sequential batches: ({aggregate.FirstBlockNum} instead of {next}).");

        await _logIndexStorage.SetReceiptsAsync(aggregate, !isForward, _stats?[isForward]);

        if (aggregate.LastBlockNum == 0)
            _receiptStorage.AnyReceiptsInserted -= OnReceiptsInserted;

        UpdateProgress();
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

            var buffer = new BlockReceipts[BatchSize];
            while (!CancellationToken.IsCancellationRequested)
            {
                if (!isForward && start < GetMinTargetBlockNumber())
                {
                    if (_logger.IsTrace)
                        _logger.Trace($"{GetLogPrefix(isForward)}: queued last block");

                    return;
                }

                var end = isForward ? start + BatchSize - 1 : Math.Max(0, start - BatchSize + 1);

                // from - inclusive, to - exclusive
                var (from, to) = isForward
                    ? (start, Math.Min(end, GetMaxTargetBlockNumber()) + 1)
                    : (end, Math.Max(start, GetMinTargetBlockNumber()) + 1);

                var timestamp = Stopwatch.GetTimestamp();
                Array.Clear(buffer);
                ReadOnlySpan<BlockReceipts> batch = GetNextBatch(from, to, buffer, isForward, CancellationToken);

                if (batch.Length == 0)
                {
                    // next = isForward ? from : to - 1;
                    //
                    // var block = _blockTree.FindBlock((long)next);
                    // var status = new
                    // {
                    //     Block = block,
                    //     HasTransactions = block?.Header.HasTransactions,
                    //     HasBlock = block == null ? (bool?)null : _receiptStorage.HasBlock(block.Number, block.Hash!),
                    //     ReceiptsLength = block == null ? null : _receiptStorage.Get(block)?.Length,
                    //     BestKnownNumber = _blockTree.BestKnownNumber
                    // };
                    // _logger.Info($"[TRACE] {GetLogPrefix(isForward)}: waiting for receipts of block {next}: {status}");

                    await Task.Delay(NewBlockWaitTimeout, CancellationToken);
                    continue;
                }

                _stats?[isForward].LoadingReceipts.Include(Stopwatch.GetElapsedTime(timestamp));

                start = GetNextBlockNumber(batch[^1].BlockNumber, isForward);
                await queue.WriteAsync(batch.ToArray(), CancellationToken);
            }
        }
        catch (Exception ex)
        {
            HandleException(ex);
        }

        if (_logger.IsInfo)
            _logger.Info($"{GetLogPrefix(isForward)}: queueing completed.");
    }

    private void UpdateProgress()
    {
        if (!_pivotTask.IsCompletedSuccessfully) return;
        var pivotNumber = _pivotTask.Result;

        if (_processingQueues is null) return;

        if (!_forwardProgressLogger.HasEnded)
        {
            _forwardProgressLogger.TargetValue = Math.Max(0, _blockTree.BestKnownNumber - MaxReorgDepth - pivotNumber + 1);
            _forwardProgressLogger.Update(_logIndexStorage.GetMaxBlockNumber() is { } max ? max - pivotNumber + 1 : 0);
            _forwardProgressLogger.CurrentQueued = _processingQueues[true].QueueCount;

            // if (_forwardProgressLogger.CurrentValue == _forwardProgressLogger.TargetValue)
            //     _forwardProgressLogger.MarkEnd();
        }

        if (!_backwardProgressLogger.HasEnded)
        {
            _backwardProgressLogger.TargetValue = pivotNumber - GetMinTargetBlockNumber();
            _backwardProgressLogger.Update(_logIndexStorage.GetMinBlockNumber() is { } min ? pivotNumber - min : 0);
            _backwardProgressLogger.CurrentQueued = _processingQueues[false].QueueCount;

            if (_backwardProgressLogger.CurrentValue >= _backwardProgressLogger.TargetValue)
            {
                _backwardProgressLogger.MarkEnd();

                if (_logger.IsInfo)
                    _logger.Info($"{GetLogPrefix(isForward: false)}: completed.");
            }
        }
    }

    private static int? GetNextBlockNumber(ILogIndexStorage storage, bool isForward)
    {
        return isForward ? storage.GetMaxBlockNumber() + 1 : storage.GetMinBlockNumber() - 1;
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
        buffer[0] = GetBlockReceipts(nextIndex);

        if (buffer[0] == default)
            return ReadOnlySpan<BlockReceipts>.Empty;

        Parallel.For(from, to, new()
        {
            CancellationToken = token,
            MaxDegreeOfParallelism = IOParallelism
        }, i =>
        {
            var bufferIndex = isForward ? i - from : to - 1 - i;
            if (buffer[bufferIndex] == default)
                buffer[bufferIndex] = GetBlockReceipts(i);
        });

        var endIndex = Array.IndexOf(buffer, default);
        return endIndex < 0 ? buffer : buffer.AsSpan(..endIndex);
    }

    // TODO: move to IReceiptStorage as `TryGet`?
    private BlockReceipts GetBlockReceipts(int i)
    {
        if (_blockTree.FindBlock(i, BlockTreeLookupOptions.ExcludeTxHashes) is not { Hash: not null } block)
            return default;

        if (!block.Header.HasTransactions)
            return new(i, []);

        TxReceipt[] receipts = _receiptStorage.Get(block) ?? [];

        if (receipts.Length == 0)
            return default;

        return new(i, receipts);
    }

    private static bool IsSeqAsc(IReadOnlyList<BlockReceipts> blocks)
    {
        int j = blocks.Count - 1;
        int i = 1, d = blocks[0].BlockNumber;
        while (i <= j && blocks[i].BlockNumber - i == d) i++;
        return i > j;
    }

    private static bool IsSeqDesc(IReadOnlyList<BlockReceipts> blocks)
    {
        int j = blocks.Count - 1;
        int i = 1, d = blocks[0].BlockNumber;
        while (i <= j && blocks[i].BlockNumber + i == d) i++;
        return i > j;
    }

    private static string GetLogPrefix(bool? isForward = null) => isForward switch
    {
        true => "Log index sync (Forward)",
        false => "Log index sync (Backward)",
        _ => "Log index sync"
    };
}
