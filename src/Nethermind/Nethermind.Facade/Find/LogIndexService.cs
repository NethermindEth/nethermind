// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Logging;
using Timer = System.Timers.Timer;

namespace Nethermind.Facade.Find;

// TODO: move to correct namespace
// TODO: reduce periodic logging
public sealed class LogIndexService : ILogIndexService
{
    private readonly IBlockTree _blockTree;
    private readonly ISyncConfig _syncConfig;
    private readonly ILogger _logger;

    private readonly CancellationTokenSource _cancellationSource = new();
    private CancellationToken CancellationToken => _cancellationSource.Token;

    // TODO: take some/all values from chain config
    private const int MaxReorgDepth = 8;
    private const int MaxCacheSize = 256;
    private const int BatchSize = 256;
    private const int MaxQueueSize = 4096;
    private static readonly int IOParallelism = Math.Max(Environment.ProcessorCount / 2, 1);
    private static readonly TimeSpan NewBlockWaitTimeout = TimeSpan.FromSeconds(5);

    private readonly ILogIndexStorage _logIndexStorage;
    private readonly IReceiptFinder _receiptFinder;
    private readonly IReceiptStorage _receiptStorage;
    private readonly ProgressLogger _forwardProgressLogger;
    private readonly ProgressLogger _backwardProgressLogger;
    private readonly Timer _progressLoggerTimer = new(TimeSpan.FromSeconds(30));

    // TODO: handle risk or potential reorg loss on restart
    private readonly Channel<BlockReceipts> _reorgChannel = Channel.CreateUnbounded<BlockReceipts>();

    private readonly Channel<BlockReceipts> _forwardChannel = Channel.CreateBounded<BlockReceipts>(MaxQueueSize);
    private readonly LruCache<int, BlockReceipts> _forwardBlockCache = new(MaxCacheSize, nameof(LogIndexService));
    private readonly AutoResetEvent _newForwardBlockEvent = new(false);

    private readonly Channel<BlockReceipts> _backwardChannel = Channel.CreateBounded<BlockReceipts>(MaxQueueSize);
    private readonly LruCache<int, BlockReceipts> _backwardBlockCache = new(MaxCacheSize, nameof(LogIndexService));
    private readonly AutoResetEvent _newBackwardBlockEvent = new(false);

    private readonly TaskCompletionSource<int> _pivotSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task<int> _pivotTask;

    private Task? _processTask;
    private Task? _queueForwardBlocksTask;
    private Task? _queueBackwardBlocksTask;

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

            var pivotNumber = await _pivotTask;

            UpdateProgress(pivotNumber);
            LogProgress();
            _progressLoggerTimer.AutoReset = true;
            _progressLoggerTimer.Elapsed += (_, _) => LogProgress();
            _progressLoggerTimer.Start();

            _queueForwardBlocksTask = Task.Run(() => DoQueueBlocks(isForward: true), CancellationToken);
            // TODO: log and don't start backward sync if old receipts download is disabled
            _queueBackwardBlocksTask = Task.Run(() => DoQueueBlocks(isForward: false), CancellationToken);
            _processTask = Task.Run(DoProcess, CancellationToken);
        }
        catch (OperationCanceledException canceledEx) when (canceledEx.CancellationToken == CancellationToken)
        {
            // Cancelled
        }
    }

    public async Task StopAsync()
    {
        await _cancellationSource.CancelAsync();
        _pivotSource.TrySetCanceled(CancellationToken);
        _progressLoggerTimer.Stop();
        await (_queueForwardBlocksTask ?? Task.CompletedTask);
        await (_queueBackwardBlocksTask ?? Task.CompletedTask);
        await (_processTask ?? Task.CompletedTask);
        await _logIndexStorage.StopAsync();
    }

    public async ValueTask DisposeAsync()
    {
        _progressLoggerTimer.Dispose();
        _newForwardBlockEvent.Dispose();
        _newBackwardBlockEvent.Dispose();
        await _logIndexStorage.DisposeAsync();
    }

    private void LogProgress()
    {
        _forwardProgressLogger.LogProgress();
        _backwardProgressLogger.LogProgress();

        if (_stats is not null)
        {
            LogIndexUpdateStats stats = Interlocked.Exchange(ref _stats, new(_logIndexStorage));

            if (_logger.IsInfo) // TODO: log at debug/trace
                _logger.Info($"{GetLogPrefix()}: {stats}");
        }
    }

    // TODO: add receipts to cache
    private void OnReceiptsInserted(object? sender, ReceiptsEventArgs args)
    {
        // if (args.WasRemoved)
        // {
        //     _reorgChannel.Writer.TryWrite(new((int)args.BlockHeader.Number, args.TxReceipts));
        //     return;
        // }

        //_logger.Info($"{nameof(OnReceiptsInserted)}: {args.BlockHeader.ToString(BlockHeader.Format.FullHashAndNumber)} [{args.TxReceipts.Length}]");

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
        return (int)Math.Max(_syncConfig.AncientReceiptsBarrierCalc, 0);
    }

    private async Task DoProcess()
    {
        try
        {
            while (!CancellationToken.IsCancellationRequested)
            {
                // var reorgQueue = _reorgChannel.Reader;
                // while (reorgQueue.TryPeek(out BlockReceipts reorgBlock) &&
                //        reorgBlock.BlockNumber <= _logIndexStorage.GetMaxBlockNumber() &&
                //        reorgQueue.TryRead(out reorgBlock))
                // {
                //     await _logIndexStorage.ReorgFrom(reorgBlock);
                // }

                await ProcessQueued(isForward: true);

                if (_logIndexStorage.GetMinBlockNumber() != GetMinTargetBlockNumber())
                    await ProcessQueued(isForward: false);
            }
        }
        catch (OperationCanceledException canceledEx) when (canceledEx.CancellationToken == CancellationToken)
        {
            // Cancelled
        }
        catch (Exception ex)
        {
            if (_logger.IsError)
                _logger.Error("Log index block addition failed. Please restart the client.", ex);
        }

        if (_logger.IsInfo)
            _logger.Info($"{GetLogPrefix()}: processing completed.");
    }

    private LogIndexUpdateStats? _stats;

    private async Task<int> ProcessQueued(bool isForward)
    {
        // var reorgQueue = _reorgChannel.Reader;
        // while (reorgQueue.TryPeek(out BlockReceipts reorgBlock) &&
        //        reorgBlock.BlockNumber <= _logIndexStorage.GetMaxBlockNumber() &&
        //        reorgQueue.TryRead(out reorgBlock))
        // {
        //     await _logIndexStorage.ReorgFrom(reorgBlock);
        // }

        var pivotNumber = await _pivotTask;
        ChannelReader<BlockReceipts> queue = isForward ? _forwardChannel.Reader : _backwardChannel.Reader;

        // await _logIndexStorage.CompactAsync(flush: false);
        // ((LogIndexStorage)_logIndexStorage).FixMinBlockNumber();

        var count = 0;
        // TODO: reuse buffer
        while (!CancellationToken.IsCancellationRequested && queue.ReadBatch(BatchSize) is { Count: > 0 } batch)
        {
            // TODO: remove check to save time?
            if ((isForward && !IsSeqAsc(batch)) ||
                (!isForward && !IsSeqDesc(batch)) ||
                (GetNextBlockNumber(isForward) is { } next && next != batch[0].BlockNumber))
            {
                throw new Exception($"Non-sequential block numbers in log index queue, batch: ({batch[0]} -> {batch[^1]}).");
            }

            // TODO: do aggregation separately and in parallel?
            _stats ??= new(_logIndexStorage);
            await _logIndexStorage.SetReceiptsAsync(batch, isBackwardSync: !isForward, _stats);

            if (_logIndexStorage.GetMinBlockNumber() == 0)
                _receiptStorage.AnyReceiptsInserted -= OnReceiptsInserted;

            UpdateProgress(pivotNumber);
            count++;
        }

        return count;
    }

    // TODO: aggregate to dictionary beforehand (in multiple threads?), should save a lot of time
    private async Task DoQueueBlocks(bool isForward)
    {
        try
        {
            var pivotNumber = await _pivotTask;

            ChannelWriter<BlockReceipts> queue = isForward ? _forwardChannel.Writer : _backwardChannel.Writer;
            AutoResetEvent newBlockEvent = isForward ? _newForwardBlockEvent : _newBackwardBlockEvent;

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
            var lastQueuedNum = -1;
            while (!CancellationToken.IsCancellationRequested)
            {
                if (!isForward && start < GetMinTargetBlockNumber())
                {
                    if (_logger.IsTrace)
                        _logger.Trace($"{GetLogPrefix(isForward)}: queued last block");

                    return;
                }

                var end = isForward ? start + BatchSize - 1 : start - BatchSize + 1;

                // from - inclusive, to - exclusive
                var (from, to) = isForward
                    ? (start, Math.Min(end, GetMaxTargetBlockNumber()) + 1)
                    : (end, Math.Max(start, GetMinTargetBlockNumber()) + 1);

                Array.Clear(buffer);
                PopulateBlocks(from, to, buffer, isForward, CancellationToken);

                if (buffer[0] == default)
                {
                    next = isForward ? from : to - 1;

                    if (_logger.IsTrace)
                        _logger.Trace($"{GetLogPrefix(isForward)}: waiting for receipts of block {next}");

                    await newBlockEvent.WaitOneAsync(NewBlockWaitTimeout, CancellationToken);
                    continue;
                }

                foreach (BlockReceipts block in buffer)
                {
                    CancellationToken.ThrowIfCancellationRequested();

                    if (block == default)
                    {
                        break;
                    }

                    if (lastQueuedNum != -1 && block.BlockNumber != GetNextBlockNumber(lastQueuedNum, isForward))
                    {
                        if (_logger.IsError)
                        {
                            _logger.Error(
                                $"Non-sequential block number {block.BlockNumber} in log index queue, previous block: {lastQueuedNum}. " +
                                $"Please restart the client."
                            );
                        }

                        return;
                    }

                    await queue.WriteAsync(block, CancellationToken);
                    lastQueuedNum = block.BlockNumber;
                    start = GetNextBlockNumber(block.BlockNumber, isForward);
                }
            }
        }
        catch (OperationCanceledException canceledEx) when (canceledEx.CancellationToken == CancellationToken)
        {
            // Cancelled
        }
        catch (Exception ex)
        {
            if (_logger.IsError)
                _logger.Error("Log index block enumeration failed. Please restart the client.", ex);
        }

        if (_logger.IsInfo)
            _logger.Info($"{GetLogPrefix(isForward)}: queueing completed.");
    }

    private void UpdateProgress(int pivotNumber)
    {
        _forwardProgressLogger.TargetValue = Math.Max(0, _blockTree.BestKnownNumber - MaxReorgDepth - pivotNumber + 1);
        _forwardProgressLogger.Update(_logIndexStorage.GetMaxBlockNumber() is { } max ? max - pivotNumber + 1 : 0);
        _forwardProgressLogger.CurrentQueued = _forwardChannel.Reader.Count;

        // if (_forwardProgressLogger.CurrentValue == _forwardProgressLogger.TargetValue)
        //     _forwardProgressLogger.MarkEnd();

        _backwardProgressLogger.TargetValue = pivotNumber - GetMinTargetBlockNumber();
        _backwardProgressLogger.Update(_logIndexStorage.GetMinBlockNumber() is { } min ? pivotNumber - min : 0);
        _backwardProgressLogger.CurrentQueued = _backwardChannel.Reader.Count;

        if (_backwardProgressLogger.CurrentValue >= _backwardProgressLogger.TargetValue)
        {
            _backwardProgressLogger.MarkEnd();
            if (_logger.IsInfo)
                _logger.Info($"{GetLogPrefix(isForward: false)}: completed.");
        }
    }

    private int? GetNextBlockNumber(bool isForward)
    {
        return isForward ? _logIndexStorage.GetMaxBlockNumber() + 1 : _logIndexStorage.GetMinBlockNumber() - 1;
    }

    private static int GetNextBlockNumber(int last, bool isForward)
    {
        return isForward ? last + 1 : last - 1;
    }

    private void PopulateBlocks(int from, int to, BlockReceipts[] buffer, bool isForward, CancellationToken token)
    {
        if (to <= from)
            return;

        if (to - from > buffer.Length)
            throw new InvalidOperationException($"Buffer size is too small: {buffer.Length} / {to - from}");

        // Check the immediate next block first
        var nextIndex = isForward ? from : to - 1;
        buffer[0] = GetBlockReceipts(nextIndex);
        if (buffer[0] == default) return;

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

    }

    // TODO: move to IReceiptStorage as `TryGet`?
    private BlockReceipts GetBlockReceipts(int i)
    {
        if (_blockTree.FindBlock(i) is not { Hash: not null } block)
            return default;

        TxReceipt[] receipts = _receiptStorage.Get(block, false) ?? [];

        // Double-check if no receipts are present
        if (receipts.Length == 0 && block.Header.HasTransactions)
            return default;

        return new(i, receipts);
    }

    private static bool IsSeqAsc(List<BlockReceipts> blocks)
    {
        int j = blocks.Count - 1;
        int i = 1, d = blocks[0].BlockNumber;
        while (i <= j && blocks[i].BlockNumber - i == d) i++;
        return i > j;
    }

    private static bool IsSeqDesc(List<BlockReceipts> blocks)
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
