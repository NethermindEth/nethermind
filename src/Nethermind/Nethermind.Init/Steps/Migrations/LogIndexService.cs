// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Timers;
using Nethermind.Api;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Synchronization.ParallelSync;
using Timer = System.Timers.Timer;

namespace Nethermind.Init.Steps.Migrations;

// TODO: move to correct namespace
public class LogIndexService : ILogIndexService
{
    private readonly IBlockTree _blockTree;
    private readonly ILogger _logger;

    private readonly CancellationTokenSource _cancellationSource = new();
    private CancellationToken CancellationToken => _cancellationSource.Token;

    // TODO: take some/all values from chain config
    private const int MaxReorgDepth = 64;
    private const int MaxCacheSize = 256;
    private const int BatchSize = 256;
    private const int MaxQueueSize = 4096;
    private static readonly int IOParallelism = Math.Max(Environment.ProcessorCount / 2, 1);
    private static readonly TimeSpan NewBlockWaitTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan QueueSpinWaitTime = TimeSpan.FromSeconds(1);

    private readonly ILogIndexStorage _logIndexStorage;
    private readonly IReceiptMonitor _receiptMonitor;
    private readonly IReceiptFinder _receiptFinder;
    private readonly IReceiptStorage _receiptStorage;
    private readonly ProgressLogger _forwardProgressLogger;
    private readonly ProgressLogger _backwardProgressLogger;
    private readonly Timer _progressLoggerTimer = new(TimeSpan.FromSeconds(30));

    // TODO: handle risk or potential reorg loss on restart
    private readonly Channel<BlockReceipts> _reorgChannel = Channel.CreateUnbounded<BlockReceipts>();

    private readonly Channel<BlockReceipts> _forwardChannel = Channel.CreateBounded<BlockReceipts>(MaxQueueSize);
    private readonly LruCache<int, BlockReceipts> _forwardBlockCache = new(MaxCacheSize, nameof(LogIndexService));
    private readonly AutoResetEvent _newForwardBlockEvent = new (false);

    private readonly Channel<BlockReceipts> _backwardChannel = Channel.CreateBounded<BlockReceipts>(MaxQueueSize);
    private readonly LruCache<int, BlockReceipts> _backwardBlockCache = new(MaxCacheSize, nameof(LogIndexService));
    private readonly AutoResetEvent _newBackwardBlockEvent = new (false);

    // TODO: wait for when stopping?
    private Task? _processTask;
    private Task? _queueForwardBlocksTask = Task.CompletedTask;
    private Task? _queueBackwardBlocksTask;
    private int _pivotNumber;

    public LogIndexService(IApiWithStores api, ISyncModeSelector syncModeSelector, IBlockTree blockTree, IReceiptFinder receiptFinder, IReceiptStorage receiptStorage)
    {
        ArgumentNullException.ThrowIfNull(api.LogIndexStorage);
        ArgumentNullException.ThrowIfNull(api.ReceiptMonitor);
        ArgumentNullException.ThrowIfNull(api.LogManager);

        _blockTree = blockTree;
        _receiptFinder = receiptFinder;
        _receiptStorage = receiptStorage;
        _logger = api.LogManager.GetClassLogger<LogIndexService>();

        _forwardProgressLogger = new("Log index sync (Forward)", api.LogManager);
        _backwardProgressLogger = new("Log index sync (Backward)", api.LogManager);

        _logIndexStorage = api.LogIndexStorage;
        _receiptMonitor = api.ReceiptMonitor;
    }

    public Task StartAsync()
    {
        _pivotNumber =
            (_logIndexStorage.GetMaxBlockNumber() - _logIndexStorage.GetMinBlockNumber()) / 2
            ?? (int)_blockTree.SyncPivot.BlockNumber;

        UpdateProgress();
        _progressLoggerTimer.AutoReset = true;
        _progressLoggerTimer.Elapsed += OnLogProgress;
        _progressLoggerTimer.Start();

        _receiptStorage.OldReceiptsInserted += OnReceiptsInserted;

        _queueForwardBlocksTask = Task.Run(() => DoQueueBlocks(isForward: true), CancellationToken);
        _queueBackwardBlocksTask = Task.Run(() => DoQueueBlocks(isForward: false), CancellationToken);
        _processTask = Task.Run(DoProcess, CancellationToken);

        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        await _cancellationSource.CancelAsync();
        await (_queueForwardBlocksTask ?? Task.CompletedTask);
        await (_queueBackwardBlocksTask ?? Task.CompletedTask);
        await (_processTask ?? Task.CompletedTask);
        await _logIndexStorage.StopAsync();
    }

    private void OnLogProgress(object? sender, ElapsedEventArgs e)
    {
        _forwardProgressLogger.LogProgress();
        _backwardProgressLogger.LogProgress();

        if (_stats is not null)
        {
            LogIndexUpdateStats stats = Interlocked.Exchange(ref _stats, new());
            TempLog($"\n{stats}");
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

        var next = args.BlockHeader.Number;
        var (min, max) = (_logIndexStorage.GetMinBlockNumber(), _logIndexStorage.GetMaxBlockNumber());

        if (min is null || next < min)
            _newBackwardBlockEvent.Set();

        if (max is null || next > max)
            _newForwardBlockEvent.Set();
    }

    private int? GetMaxAvailableBlockNumber() => (int)Math.Max(_blockTree.BestKnownNumber, _blockTree.BestKnownBeaconNumber);
    private int? GetMinAvailableBlockNumber() => (int?)_blockTree.LowestInsertedHeader?.Number;

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

                var count = await ProcessQueued(isForward: true);

                if (_logIndexStorage.GetMinBlockNumber() != 0)
                    count += await ProcessQueued(isForward: false);
                else
                    _backwardProgressLogger.MarkEnd();

                if (count == 0)
                    await Task.Delay(QueueSpinWaitTime, CancellationToken);
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

        ChannelReader<BlockReceipts> queue = isForward ? _forwardChannel.Reader : _backwardChannel.Reader;

        // await _logIndexStorage.CompactAsync(flush: false);
        // ((LogIndexStorage)_logIndexStorage).FixMinBlockNumber();

        var count = 0;
        // TODO: reuse buffer
        while (queue.ReadBatch(BatchSize) is { Length: > 0 } batch)
        {
            // var (isValid, shouldAdd) = CheckNextBatch(batch, isForward);
            // if (!isValid) return count;
            // if (!shouldAdd) continue;

            // TODO: remove check to save time?
            if ((isForward && !IsAsc(batch)) || (!isForward && !IsDesc(batch)) ||
                batch[0].BlockNumber != (GetNextBlockNumber(isForward) ?? batch[0].BlockNumber))
            {
                throw new Exception($"Non-sequential block numbers in log index queue, batch: ({batch[0]} -> {batch[^1]}).");
            }

            // TODO: do aggregation separately and in parallel?
            _stats ??= new();
            await _logIndexStorage.SetReceiptsAsync(batch, isBackwardSync: !isForward, _stats);

            UpdateProgress();
            count++;
        }

        return count;
    }

    private (bool isValid, bool shouldAdd) CheckNextBatch(BlockReceipts[] batch, bool isForward)
    {
        var lastBlockOrNull = isForward ? _logIndexStorage.GetMaxBlockNumber() : _logIndexStorage.GetMinBlockNumber();

        if (lastBlockOrNull is not { } lastBlockNum)
            return (isValid: true, shouldAdd: true);

        BlockReceipts nextBlock = batch[0];
        switch (isForward)
        {
            case true when nextBlock.BlockNumber <= lastBlockNum:
            case false when nextBlock.BlockNumber >= lastBlockNum:
            {
                if (_logger.IsWarn)
                {
                    _logger.Warn($"Skipping duplicate block number {nextBlock.BlockNumber} in log index queue, last added: {lastBlockNum}");
                }

                return (isValid: true, shouldAdd: false);
            }
            case true when nextBlock.BlockNumber > GetNextBlockNumber(true):
            case false when nextBlock.BlockNumber < GetNextBlockNumber(false):
            {
                if (_logger.IsError)
                {
                    _logger.Error(
                        $"Non-sequential block number {nextBlock.BlockNumber} in log index queue, last added: {lastBlockNum}. " +
                        $"Please restart the client."
                    );
                }

                return (isValid: false, shouldAdd: false);
            }
            default:
                return (isValid: true, shouldAdd: true);
        }
    }

    // TODO: aggregate to dictionary beforehand (in multiple threads?), should save a lot of time
    private async Task DoQueueBlocks(bool isForward)
    {
        try
        {
            ChannelWriter<BlockReceipts> queue = isForward ? _forwardChannel.Writer : _backwardChannel.Writer;
            AutoResetEvent newBlockEvent = isForward ? _newForwardBlockEvent : _newBackwardBlockEvent;

            var next = GetNextBlockNumber(isForward);
            if (next is not { } start)
            {
                if (isForward)
                {
                    start = _pivotNumber;
                }
                else
                {
                    // Wait until we start updating index forward
                    // Post-pivot forward-sync should start first
                    //await _logIndexStorage.FirstBlockAdded;
                    //start = GetNextBlockNumber(false)!.Value;
                    start = _pivotNumber - 1;
                }
            }

            var buffer = new BlockReceipts[BatchSize];
            var lastQueuedNum = -1;
            while (!CancellationToken.IsCancellationRequested)
            {
                if (!isForward && start < 0)
                {
                    if (_logger.IsTrace)
                        _logger.Trace("Queued last block for log index backward sync.");

                    return;
                }

                var end = isForward
                    // TODO: do not stay MaxReorgDepth behind, handle reorgs instead
                    ? Math.Min(GetMaxAvailableBlockNumber() - MaxReorgDepth ?? int.MinValue, start + BatchSize - 1)
                    : Math.Max(start - BatchSize + 1, GetMinAvailableBlockNumber() ?? int.MaxValue);

                // from - inclusive, to - exclusive
                var (from, to) = isForward ? (start, end + 1) : (end, start + 1);

                if (to <= from)
                {
                    TempLog($"{to} <= {from} ({isForward}), waiting for new block");
                    await newBlockEvent.WaitOneAsync(NewBlockWaitTimeout, CancellationToken);
                    continue;
                }

                Array.Clear(buffer);
                PopulateBlocks(from, to, buffer, isForward, CancellationToken);

                if (buffer[0] == default)
                {
                    TempLog($"No new blocks fetched ({from} - {to}, {isForward}), waiting for new block");
                    await newBlockEvent.WaitOneAsync(NewBlockWaitTimeout, CancellationToken);
                    continue;
                }

                foreach (BlockReceipts block in buffer)
                {
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
    }

    private void UpdateProgress()
    {
        UpdateProgress(_forwardProgressLogger);
        UpdateProgress(_backwardProgressLogger);
    }

    private void UpdateProgress(ProgressLogger progress)
    {
        if (progress == _forwardProgressLogger)
        {
            _forwardProgressLogger.TargetValue = GetMaxAvailableBlockNumber() ?? 0 - _pivotNumber;
            _forwardProgressLogger.Update((_logIndexStorage.GetMaxBlockNumber() ?? _pivotNumber) - _pivotNumber);
            _forwardProgressLogger.CurrentQueued = _forwardChannel.Reader.Count;

            // if (_forwardProgressLogger.CurrentValue == _forwardProgressLogger.TargetValue)
            //     _forwardProgressLogger.MarkEnd();
        }
        else if (progress == _backwardProgressLogger)
        {
            _backwardProgressLogger.TargetValue = _pivotNumber;
            _backwardProgressLogger.Update(_pivotNumber - (_logIndexStorage.GetMinBlockNumber() ?? _pivotNumber));
            _backwardProgressLogger.CurrentQueued = _backwardChannel.Reader.Count;

            if (_backwardProgressLogger.CurrentValue == _backwardProgressLogger.TargetValue)
                _backwardProgressLogger.MarkEnd();
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

        Parallel.For(from, to, new()
        {
            CancellationToken = token,
            MaxDegreeOfParallelism = IOParallelism
        }, i =>
        {
            Block? block = _blockTree.FindBlock(i);
            if (block == null) return;
            TxReceipt[] receipts = _receiptStorage.Get(block, false) ?? [];
            var index = isForward ? i - from : to - 1 - i;
            buffer[index] = new(i, receipts);
        });
    }

    private static bool IsAsc(BlockReceipts[] blocks)
    {
        int j = blocks.Length - 1;
        if (j < 1) return true;
        int ai = blocks[0].BlockNumber, i = 1;
        while (i <= j && ai < (ai = blocks[i].BlockNumber)) i++;
        return i > j;
    }

    private static bool IsDesc(BlockReceipts[] blocks)
    {
        int j = blocks.Length - 1;
        if (j < 1) return true;
        int ai = blocks[0].BlockNumber, i = 1;
        while (i <= j && ai > (ai = blocks[i].BlockNumber)) i++;
        return i > j;
    }

    // TODO: remove/revise!
    private void TempLog(string message) => _logger.Warn($"LogIndexStorage: {message}");
}
