// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
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
// TODO: reduce periodic logging
public sealed class LogIndexService : ILogIndexService
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
    private static readonly TimeSpan NewBlockWaitTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan QueueSpinWaitTime = TimeSpan.FromSeconds(1);

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

    // TODO: wait for when stopping?
    private Task? _processTask;
    private Task? _queueForwardBlocksTask;
    private Task? _queueBackwardBlocksTask;
    private int _pivotNumber;

    public LogIndexService(IApiWithStores api, ISyncModeSelector syncModeSelector, IBlockTree blockTree, IReceiptFinder receiptFinder, IReceiptStorage receiptStorage)
    {
        ArgumentNullException.ThrowIfNull(api.LogIndexStorage);
        ArgumentNullException.ThrowIfNull(api.LogManager);

        _blockTree = blockTree;
        _receiptFinder = receiptFinder;
        _receiptStorage = receiptStorage;
        _logger = api.LogManager.GetClassLogger<LogIndexService>();

        _forwardProgressLogger = new(GetLogPrefix(isForward: true), api.LogManager);
        _backwardProgressLogger = new(GetLogPrefix(isForward: false), api.LogManager);

        _logIndexStorage = api.LogIndexStorage;
    }

    public Task StartAsync()
    {
        _pivotNumber = _logIndexStorage.GetMaxBlockNumber() ?? (int)_blockTree.SyncPivot.BlockNumber;

        UpdateProgress();
        LogProgress();
        _progressLoggerTimer.AutoReset = true;
        _progressLoggerTimer.Elapsed += (_, _) => LogProgress();
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
            LogIndexUpdateStats stats = Interlocked.Exchange(ref _stats, new());

            if (_logger.IsInfo)
                _logger.Info($"{GetLogPrefix()}:\n{stats}");
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

    // TODO: figure out values that would be correct in all cases
    private int? GetMaxAvailableBlockNumber()
    {
        if (_blockTree.BestPersistedState is null || _blockTree.Head is not { } head)
            return null;

        var res = Math.Min(
            head.Number,
            Math.Max(_blockTree.BestKnownNumber, _blockTree.BestKnownBeaconNumber)
        );

        res -= MaxReorgDepth; // TODO: do not stay MaxReorgDepth behind, handle reorgs instead
        return res >= 0 ? (int)res : null;
    }

    private int? GetMinAvailableBlockNumber()
    {
        if (_blockTree.BestPersistedState is null || _blockTree.Head is null)
            return null;

        return (int?)_blockTree.LowestInsertedHeader?.Number;
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
        while (!CancellationToken.IsCancellationRequested && queue.ReadBatch(BatchSize) is { Length: > 0 } batch)
        {
            // TODO: remove check to save time?
            if ((isForward && !IsSeqAsc(batch)) ||
                (!isForward && !IsSeqDesc(batch)) ||
                (GetNextBlockNumber(isForward) is { } next && next != batch[0].BlockNumber))
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
                        _logger.Trace($"{GetLogPrefix(isForward)}: queued last block");

                    return;
                }

                var end = isForward
                    ? Math.Min(GetMaxAvailableBlockNumber() ?? int.MinValue, start + BatchSize - 1)
                    : Math.Max(start - BatchSize + 1, GetMinAvailableBlockNumber() ?? int.MaxValue);

                // from - inclusive, to - exclusive
                var (from, to) = isForward ? (start, end + 1) : (end, start + 1);

                if (to <= from)
                {
                    var timedOut = !await newBlockEvent.WaitOneAsync(NewBlockWaitTimeout, CancellationToken);

                    if (timedOut && _logger.IsInfo)
                    {
                        (int? synced, int? available) = GetStatus(isForward);
                        _logger.Info($"{GetLogPrefix(isForward)}: waiting for a new block, synced: {synced:N0}, available: {available:N0}");
                    }

                    continue;
                }

                Array.Clear(buffer);
                PopulateBlocks(from, to, buffer, isForward, CancellationToken);

                if (buffer[0] == default)
                {
                    var timedOut = !await newBlockEvent.WaitOneAsync(NewBlockWaitTimeout, CancellationToken);

                    if (timedOut && _logger.IsInfo)
                    {
                        var index = isForward ? from : to - 1;
                        _logger.Info($"{GetLogPrefix(isForward)}: waiting for a new block, no receipts available for {index:N0}");
                    }

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
                                $"Non-sequential block number {block.BlockNumber:N0} in log index queue, previous block: {lastQueuedNum}. " +
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
        _forwardProgressLogger.TargetValue = (GetMaxAvailableBlockNumber() ?? _pivotNumber) - _pivotNumber;
        _forwardProgressLogger.Update((_logIndexStorage.GetMaxBlockNumber() ?? _pivotNumber) - _pivotNumber);
        _forwardProgressLogger.CurrentQueued = _forwardChannel.Reader.Count;

        // if (_forwardProgressLogger.CurrentValue == _forwardProgressLogger.TargetValue)
        //     _forwardProgressLogger.MarkEnd();

        _backwardProgressLogger.TargetValue = _pivotNumber;
        _backwardProgressLogger.Update(_pivotNumber - (_logIndexStorage.GetMinBlockNumber() ?? _pivotNumber));
        _backwardProgressLogger.CurrentQueued = _backwardChannel.Reader.Count;

        if (_backwardProgressLogger.CurrentValue == _backwardProgressLogger.TargetValue)
            _backwardProgressLogger.MarkEnd();
    }

    private int? GetNextBlockNumber(bool isForward)
    {
        return isForward ? _logIndexStorage.GetMaxBlockNumber() + 1 : _logIndexStorage.GetMinBlockNumber() - 1;
    }

    private static int GetNextBlockNumber(int last, bool isForward)
    {
        return isForward ? last + 1 : last - 1;
    }

    private (int? synced, int? available) GetStatus(bool isForward) => isForward
        ? (_logIndexStorage.GetMaxBlockNumber(), GetMaxAvailableBlockNumber())
        : (_logIndexStorage.GetMinBlockNumber(), GetMinAvailableBlockNumber());

    private void PopulateBlocks(int from, int to, BlockReceipts[] buffer, bool isForward, CancellationToken token)
    {
        if (to <= from)
            return;

        if (to - from > buffer.Length)
            throw new InvalidOperationException($"Buffer size is too small: {buffer.Length} / {to - from}");

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

    private static bool IsSeqAsc(BlockReceipts[] blocks)
    {
        int j = blocks.Length - 1;
        int i = 1, d = blocks[0].BlockNumber;
        while (i <= j && blocks[i].BlockNumber - i == d) i++;
        return i > j;
    }

    private static bool IsSeqDesc(BlockReceipts[] blocks)
    {
        int j = blocks.Length - 1;
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
