// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
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
    private sealed class ProcessingQueue(
        TransformBlock<IReadOnlyList<BlockReceipts>, LogIndexAggregate> aggregateBlock,
        ActionBlock<LogIndexAggregate> addReceiptsBlock)
    {
        public int QueueCount => aggregateBlock.InputCount + addReceiptsBlock.InputCount;
        public Task WriteAsync(IReadOnlyList<BlockReceipts> batch, CancellationToken cancellation) => aggregateBlock.SendAsync(batch, cancellation);
        public Task Completion => Task.WhenAll(aggregateBlock.Completion, addReceiptsBlock.Completion);
    }

    private struct DirectionState()
    {
        public ProcessingQueue? Queue;
        public ProgressLogger? Progress;
        public readonly TaskCompletionSource Completion = new(RunContinuationsAsynchronously);
    }

    [InlineArray(2)]
    private struct DirectionStates
    {
        private DirectionState _element;
    }

    private readonly IBlockTree _blockTree;
    private readonly ISyncConfig _syncConfig;
    private readonly ILogger _logger;

    private readonly CancellationTokenSource _cancellationSource = new();
    private CancellationToken CancellationToken => _cancellationSource.Token;

    private ulong MaxReorgDepth => _config.MaxReorgDepth!.Value;
    private static readonly TimeSpan NewBlockWaitTimeout = TimeSpan.FromSeconds(5);

    private readonly ILogIndexStorage _logIndexStorage;
    private readonly ILogIndexConfig _config;
    private readonly IReceiptStorage _receiptStorage;
    private readonly ILogManager _logManager;
    private Timer? _progressLoggerTimer;

    private readonly TaskCompletionSource<ulong> _pivotSource = new(RunContinuationsAsynchronously);
    private readonly Task<ulong> _pivotTask;

    private readonly List<Task> _tasks = [];

    private DirectionStates _directions;

    private ref DirectionState Direction(bool isForward) => ref _directions[isForward ? 1 : 0];

    private LogIndexUpdateStats _stats;

    public string Description => "log index builder";

    public Task BackwardSyncCompletion => Direction(isForward: false).Completion.Task;

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

        Direction(isForward: false) = new();
        Direction(isForward: true) = new();
    }

    private void StartProcessing(bool isForward)
    {
        // Skip backward sync if storage already reached the target. Null MinBlockNumber
        // means nothing indexed yet — must not skip.
        if (!isForward && _logIndexStorage.MinBlockNumber is { } minStored && (ulong)minStored <= MinTargetBlockNumber)
        {
            MarkCompleted(false);
            return;
        }

        ref DirectionState dir = ref Direction(isForward);
        dir.Queue = BuildQueue(isForward);
        dir.Progress = new(GetLogPrefix(isForward), _logManager);

        _tasks.AddRange(
            Task.Run(() => DoQueueBlocks(isForward), CancellationToken),
            dir.Queue.Completion
        );
    }

    public async Task StartAsync()
    {
        try
        {
            if (!_config.Enabled)
                return;

            _receiptStorage.ReceiptsInserted += OnReceiptsInserted;

            TrySetPivot(_logIndexStorage.MaxBlockNumber is { } storedMax ? (ulong)storedMax : null);
            TrySetPivot(_blockTree.SyncPivot.BlockNumber);

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

        await SignalStopAsync();

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
    }

    private async Task SignalStopAsync()
    {
        await _cancellationSource.CancelAsync();
        _pivotSource.TrySetCanceled(CancellationToken);
        _progressLoggerTimer?.Stop();
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
        Direction(isForward: false).Progress?.LogProgress();
        Direction(isForward: true).Progress?.LogProgress();
    }

    private bool TrySetPivot(ulong? blockNumber)
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
        if (TrySetPivot(args.BlockHeader.Number))
            _receiptStorage.ReceiptsInserted -= OnReceiptsInserted;
    }

    public ulong MaxTargetBlockNumber => _blockTree.BestKnownNumber >= MaxReorgDepth
        ? _blockTree.BestKnownNumber - MaxReorgDepth
        : 0UL;

    // Block 0 should always be present
    public ulong MinTargetBlockNumber => _syncConfig.AncientReceiptsBarrierCalc <= 1
        ? 0UL
        : _syncConfig.AncientReceiptsBarrierCalc;

    public bool IsRunning { get; private set; }
    public DateTimeOffset? LastUpdate { get; private set; }
    public Exception? LastError { get; private set; }

    private ProcessingQueue BuildQueue(bool isForward)
    {
        TransformBlock<IReadOnlyList<BlockReceipts>, LogIndexAggregate> aggregateBlock = new(
            batch => Aggregate(batch, isForward),
            new()
            {
                BoundedCapacity = _config.MaxAggregationQueueSize,
                MaxDegreeOfParallelism = _config.MaxAggregationParallelism,
                CancellationToken = CancellationToken,
                SingleProducerConstrained = true
            }
        );

        ActionBlock<LogIndexAggregate> addReceiptsBlock = new(
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
            exception = a.Flatten().InnerException;

        if (exception is OperationCanceledException oc && oc.CancellationToken == CancellationToken)
            return; // Cancelled

        if (_logger.IsError)
            _logger.Error($"{GetLogPrefix()} failed. Please restart the client.", exception);

        LastError = exception;

        Direction(isForward: false).Completion.TrySetException(exception!);
        Direction(isForward: true).Completion.TrySetException(exception!);

        if (!isStopping)
        {
            await SignalStopAsync();
        }
    }

    private LogIndexAggregate Aggregate(IReadOnlyList<BlockReceipts> batch, bool isForward) => _logIndexStorage.Aggregate(batch, !isForward, _stats);

    private async Task AddReceiptsAsync(LogIndexAggregate aggregate, bool isForward)
    {
        if (GetNextBlockNumber(_logIndexStorage, isForward) is { } next && next != aggregate.FirstBlockNum)
            throw new($"{GetLogPrefix(isForward)}: non sequential batches: ({aggregate.FirstBlockNum} instead of {next}).");

        await _logIndexStorage.AddReceiptsAsync(aggregate, _stats);
        LastUpdate = DateTimeOffset.Now;

        UpdateProgress();

        if (_logIndexStorage.MinBlockNumber <= (int)MinTargetBlockNumber)
            MarkCompleted(false);
    }

    private async Task DoQueueBlocks(bool isForward)
    {
        try
        {
            ulong pivotNumber = await _pivotTask;

            ProcessingQueue queue = Direction(isForward).Queue!;

            int start = GetNextBlockNumber(isForward) ?? (isForward ? (int)pivotNumber : (int)pivotNumber - 1);

            BlockReceipts[] buffer = new BlockReceipts[_config.MaxBatchSize];
            while (!CancellationToken.IsCancellationRequested)
            {
                if (!isForward && start < (int)MinTargetBlockNumber)
                {
                    if (_logger.IsTrace)
                        _logger.Trace($"{GetLogPrefix(isForward)}: queued last block");

                    return;
                }

                int batchSize = _config.MaxBatchSize;
                int end = isForward ? start + batchSize - 1 : start - batchSize + 1;
                end = Math.Max(end, (int)MinTargetBlockNumber);
                end = Math.Min(end, (int)MaxTargetBlockNumber);

                // from - inclusive, to - exclusive
                (int from, int to) = isForward
                    ? (start, end + 1)
                    : (end, start + 1);

                long timestamp = Stopwatch.GetTimestamp();
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
        ulong pivotNumber = _pivotTask.Result;

        ref DirectionState forward = ref Direction(isForward: true);
        if (forward.Progress is { HasEnded: false } forwardProgress)
        {
            ulong bestKnown = _blockTree.BestKnownNumber;
            forwardProgress.TargetValue = bestKnown >= MaxReorgDepth + pivotNumber
                ? bestKnown - MaxReorgDepth - pivotNumber + 1UL
                : 0UL;
            forwardProgress.Update(_logIndexStorage.MaxBlockNumber is { } max && (ulong)max >= pivotNumber
                ? (ulong)max - pivotNumber + 1UL
                : 0UL);
            forwardProgress.CurrentQueued = forward.Queue!.QueueCount;
        }

        ref DirectionState backward = ref Direction(isForward: false);
        if (backward.Progress is { HasEnded: false } backwardProgress)
        {
            backwardProgress.TargetValue = pivotNumber >= MinTargetBlockNumber
                ? pivotNumber - MinTargetBlockNumber
                : 0UL;
            backwardProgress.Update(_logIndexStorage.MinBlockNumber is { } min && pivotNumber >= (ulong)min
                ? pivotNumber - (ulong)min
                : 0UL);
            backwardProgress.CurrentQueued = backward.Queue!.QueueCount;
        }
    }

    private void MarkCompleted(bool isForward)
    {
        ref DirectionState dir = ref Direction(isForward);
        if (!dir.Completion.TrySetResult())
            return;

        dir.Progress?.MarkEnd();

        if (_logger.IsInfo)
            _logger.Info($"{GetLogPrefix(isForward)}: completed.");
    }

    private static int? GetNextBlockNumber(ILogIndexStorage storage, bool isForward) => isForward ? storage.MaxBlockNumber + 1 : storage.MinBlockNumber - 1;

    private int? GetNextBlockNumber(bool isForward) => GetNextBlockNumber(_logIndexStorage, isForward);

    private static int GetNextBlockNumber(int last, bool isForward) => isForward ? last + 1 : last - 1;

    private ReadOnlySpan<BlockReceipts> GetNextBatch(int from, int to, BlockReceipts[] buffer, bool isForward, CancellationToken token)
    {
        if (to <= from)
            return ReadOnlySpan<BlockReceipts>.Empty;

        if (to - from > buffer.Length)
            throw new InvalidOperationException($"{GetLogPrefix()}: buffer size is too small: {buffer.Length} / {to - from}");

        int nextIndex = isForward ? from : to - 1;
        if (!TryGetBlockReceipts((ulong)nextIndex, out buffer[0]))
            return ReadOnlySpan<BlockReceipts>.Empty;

        Parallel.For(from, to, new()
        {
            CancellationToken = token,
            MaxDegreeOfParallelism = _config.MaxReceiptsParallelism
        }, i =>
        {
            int bufferIndex = isForward ? i - from : to - 1 - i;
            if (buffer[bufferIndex] == default)
                TryGetBlockReceipts((ulong)i, out buffer[bufferIndex]);
        });

        int endIndex = Array.IndexOf(buffer, default);
        return endIndex < 0 ? buffer : buffer.AsSpan(..endIndex);
    }

    // TODO: move to IReceiptStorage?
    private bool TryGetBlockReceipts(ulong blockNumber, out BlockReceipts blockReceipts)
    {
        blockReceipts = default;

        if (_blockTree.FindBlock(blockNumber, BlockTreeLookupOptions.ExcludeTxHashes) is not { Hash: not null } block)
        {
            return false;
        }

        if (!block.Header.HasTransactions)
        {
            blockReceipts = new((int)blockNumber, []);
            return true;
        }

        TxReceipt[] receipts = _receiptStorage.Get(block) ?? [];

        if (receipts.Length == 0)
        {
            return false; // block should have transactions but nothing in storage
        }

        blockReceipts = new((int)blockNumber, receipts);
        return true;
    }

    private static string GetLogPrefix(bool? isForward = null) => isForward switch
    {
        true => "Log index sync (Forward)",
        false => "Log index sync (Backward)",
        _ => "Log index sync"
    };
}
