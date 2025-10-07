// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Config;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Scheduler;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Repositories;

[assembly: InternalsVisibleTo("Nethermind.History.Test")]

namespace Nethermind.History;

public class HistoryPruner : IHistoryPruner
{
    private const int MaxOptimisticSearchAttempts = 3;
    private const int LockWaitTimeoutMs = 100;

    // only one pruning and one searching thread at a time
    private readonly object _pruneLock = new();
    private readonly object _searchLock = new();

    private ulong? _lastPrunedTimestamp;
    private readonly ILogger _logger;
    private readonly IBlockTree _blockTree;
    private readonly IReceiptStorage _receiptStorage;
    private readonly IChainLevelInfoRepository _chainLevelInfoRepository;
    private readonly IDb _metadataDb;
    private readonly IProcessExitSource _processExitSource;
    private readonly IBackgroundTaskScheduler _backgroundTaskScheduler;
    private readonly IHistoryConfig _historyConfig;
    private readonly bool _enabled;
    private readonly long _epochLength;
    private readonly long _pruningInterval;
    private readonly long _minHistoryRetentionEpochs;
    private readonly int _deletionProgressLoggingInterval;
    private readonly long _ancientBarrier;
    private long _deletePointer = 1;
    private BlockHeader? _deletePointerHeader;
    private long _lastSavedDeletePointer = 1;
    private long? _cutoffPointer;
    private ulong? _cutoffTimestamp;
    private bool _hasLoadedDeletePointer = false;

    public event EventHandler<OnNewOldestBlockArgs>? NewOldestBlock;

    public class HistoryPrunerException(string message, Exception? innerException = null) : Exception(message, innerException);

    public HistoryPruner(
        IBlockTree blockTree,
        IReceiptStorage receiptStorage,
        ISpecProvider specProvider,
        IChainLevelInfoRepository chainLevelInfoRepository,
        IDbProvider dbProvider,
        IHistoryConfig historyConfig,
        IBlocksConfig blocksConfig,
        ISyncConfig syncConfig,
        IProcessExitSource processExitSource,
        IBackgroundTaskScheduler backgroundTaskScheduler,
        IBlockProcessingQueue blockProcessingQueue,
        ILogManager logManager)
    {
        _logger = logManager.GetClassLogger();
        _deletionProgressLoggingInterval = _logger.IsDebug ? 5 : 100000;
        _blockTree = blockTree;
        _receiptStorage = receiptStorage;
        _chainLevelInfoRepository = chainLevelInfoRepository;
        _metadataDb = dbProvider.MetadataDb;
        _processExitSource = processExitSource;
        _backgroundTaskScheduler = backgroundTaskScheduler;
        _historyConfig = historyConfig;
        _enabled = historyConfig.Enabled();
        _epochLength = (long)blocksConfig.SecondsPerSlot * 32; // must be changed if slot length changes
        _pruningInterval = historyConfig.PruningInterval * 32;
        _minHistoryRetentionEpochs = specProvider.GenesisSpec.MinHistoryRetentionEpochs;

        CheckConfig();

        if (_enabled)
        {
            if (historyConfig.Pruning == PruningModes.UseAncientBarriers)
            {
                _ancientBarrier = long.Min(syncConfig.AncientBodiesBarrierCalc, syncConfig.AncientReceiptsBarrierCalc);
                Metrics.PruningCutoffBlocknumber = _ancientBarrier;
                Metrics.PruningCutoffTimestamp = null;
            }

            blockProcessingQueue.ProcessingQueueEmpty += OnBlockProcessorQueueEmpty;
        }
    }

    public long? CutoffBlockNumber
    {
        get
        {
            if (!_enabled)
            {
                return null;
            }

            if (_historyConfig.Pruning == PruningModes.UseAncientBarriers)
            {
                return _ancientBarrier;
            }

            ulong? cutoffTimestamp = CalculateCutoffTimestamp();

            if (cutoffTimestamp is null)
            {
                return null;
            }

            long? cutoffBlockNumber = null;
            long searchCutoff = _blockTree.Head is null ? _blockTree.SyncPivot.BlockNumber : _blockTree.Head.Number;
            bool lockTaken = false;
            try
            {
                Monitor.TryEnter(_searchLock, LockWaitTimeoutMs, ref lockTaken);

                if (lockTaken)
                {
                    // cutoff is unchanged, can reuse
                    if (_cutoffTimestamp is not null && cutoffTimestamp == _cutoffTimestamp)
                    {
                        return _cutoffPointer;
                    }

                    // optimisticly search a few blocks from old pointer
                    if (_cutoffPointer is not null)
                    {
                        int attempts = 0;
                        _ = GetBlocksByNumber(_cutoffPointer.Value, searchCutoff, b =>
                        {
                            if (attempts >= MaxOptimisticSearchAttempts)
                            {
                                return true;
                            }

                            bool afterCutoff = b.Timestamp >= cutoffTimestamp;
                            if (afterCutoff)
                            {
                                cutoffBlockNumber = b.Number;
                            }
                            attempts++;
                            return afterCutoff;
                        }).ToList();
                    }

                    // if linear search fails fallback to  binary search
                    cutoffBlockNumber ??= BlockTree.BinarySearchBlockNumber(_deletePointer, searchCutoff, (n, _) =>
                    {
                        BlockInfo[]? blockInfos = _chainLevelInfoRepository.LoadLevel(n)?.BlockInfos;

                        if (blockInfos is null)
                        {
                            return false;
                        }

                        foreach (BlockInfo blockInfo in blockInfos)
                        {
                            Block? b = _blockTree.FindBlock(blockInfo.BlockHash, BlockTreeLookupOptions.None, n);
                            if (b is not null && b.Timestamp >= cutoffTimestamp)
                            {
                                return true;
                            }
                        }

                        return false;
                    }, BlockTree.BinarySearchDirection.Down);

                    _cutoffTimestamp = cutoffTimestamp;
                    _cutoffPointer = cutoffBlockNumber ?? _cutoffPointer;
                    Metrics.PruningCutoffTimestamp = cutoffTimestamp;
                    Metrics.PruningCutoffBlocknumber = _cutoffPointer;
                }
                else
                {
                    return null;
                }
            }
            finally
            {
                if (lockTaken)
                    Monitor.Exit(_searchLock);
            }

            return cutoffBlockNumber;
        }
    }

    public BlockHeader? OldestBlockHeader
    {
        get
        {
            if (!_hasLoadedDeletePointer)
            {
                bool lockTaken = false;
                // take lock before updating delete pointer
                // avoids race conditions with pruning
                try
                {
                    Monitor.TryEnter(_pruneLock, LockWaitTimeoutMs, ref lockTaken);
                    if (lockTaken)
                    {
                        if (!TryLoadDeletePointer())
                        {
                            return null;
                        }
                    }
                    else
                    {
                        return null;
                    }
                }
                finally
                {
                    if (lockTaken)
                        Monitor.Exit(_pruneLock);
                }
            }

            return _deletePointerHeader;
        }
    }

    private void OnBlockProcessorQueueEmpty(object? sender, EventArgs e)
        => SchedulePruneHistory(_processExitSource.Token);

    private void SchedulePruneHistory(CancellationToken cancellationToken)
        => _backgroundTaskScheduler.ScheduleTask(1,
            (_, backgroundTaskToken) =>
            {
                var cts = CancellationTokenSource.CreateLinkedTokenSource(backgroundTaskToken, cancellationToken);
                return TryPruneHistory(cts.Token);
            });

    internal Task TryPruneHistory(CancellationToken cancellationToken)
    {
        bool lockTaken = false;
        try
        {
            Monitor.TryEnter(_pruneLock, LockWaitTimeoutMs, ref lockTaken);
            if (lockTaken)
            {
                if (_blockTree.Head is null ||
                    _blockTree.SyncPivot.BlockNumber == 0 ||
                    !TryLoadDeletePointer() ||
                    !ShouldPruneHistory(out ulong? cutoffTimestamp))
                {
                    if (_logger.IsDebug) _logger.Debug($"Skipping historical block pruning.");
                    return Task.CompletedTask;
                }

                if (_logger.IsInfo)
                {
                    long? cutoff = CutoffBlockNumber;
                    cutoff = cutoff is null ? null : long.Min(cutoff!.Value, _blockTree.SyncPivot.BlockNumber);
                    long? toDelete = cutoff - _deletePointer;

                    string cutoffString = cutoffTimestamp is null ? $"#{(cutoff is null ? "unknown" : cutoff)}" : $"timestamp {cutoffTimestamp} (#{(cutoff is null ? "unknown" : cutoff)})";
                    _logger.Info($"Pruning historical blocks up to {cutoffString}. Estimated {(toDelete is null ? "unknown" : toDelete)} blocks will be deleted.");
                }

                PruneBlocksAndReceipts(cutoffTimestamp, cancellationToken);
            }
            else if (_logger.IsDebug)
            {
                _logger.Debug("Skipping historical pruning, task already running.");
            }
        }
        finally
        {
            if (lockTaken)
                Monitor.Exit(_pruneLock);
        }

        return Task.CompletedTask;
    }

    internal bool SetDeletePointerToOldestBlock()
    {
        long? oldestBlockNumber = BlockTree.BinarySearchBlockNumber(1L, _blockTree.SyncPivot.BlockNumber, BlockExists, BlockTree.BinarySearchDirection.Down);

        if (oldestBlockNumber is not null)
        {
            UpdateDeletePointer(oldestBlockNumber.Value);
            SaveDeletePointer();
            return true;
        }

        return false;
    }

    private bool BlockExists(long n, bool _)
    {
        ChainLevelInfo? info = _chainLevelInfoRepository.LoadLevel(n);

        if (info is null)
        {
            return false;
        }

        foreach (BlockInfo blockInfo in info.BlockInfos)
        {
            Block? b = _blockTree.FindBlock(blockInfo.BlockHash, n);
            if (b is not null)
            {
                return true;
            }
        }

        return false;
    }

    private void CheckConfig()
    {
        if (_historyConfig.RetentionEpochs < _minHistoryRetentionEpochs)
        {
            throw new HistoryPrunerException($"HistoryRetentionEpochs must be at least {_minHistoryRetentionEpochs}.");
        }
    }

    private bool ShouldPruneHistory(out ulong? cutoffTimestamp)
    {
        cutoffTimestamp = null;

        if (!_enabled || (_pruningInterval != 0 && _blockTree.Head!.Number % _pruningInterval != 0))
        {
            return false;
        }

        if (_historyConfig.Pruning == PruningModes.UseAncientBarriers)
        {
            return _deletePointer < _ancientBarrier;
        }

        cutoffTimestamp = CalculateCutoffTimestamp();
        return cutoffTimestamp is not null && (_lastPrunedTimestamp is null || cutoffTimestamp > _lastPrunedTimestamp);
    }

    private void PruneBlocksAndReceipts(ulong? cutoffTimestamp, CancellationToken cancellationToken)
    {
        int deletedBlocks = 0;
        ulong? lastDeletedTimstamp = null;
        try
        {
            IEnumerable<Block> blocks = _historyConfig.Pruning == PruningModes.UseAncientBarriers ?
                GetBlocksBeforeAncientBarrier() :
                GetBlocksBeforeTimestamp(cutoffTimestamp!.Value);
            foreach (Block block in blocks)
            {
                long number = block.Number;
                Hash256 hash = block.Hash!;

                if (cancellationToken.IsCancellationRequested)
                {
                    if (_logger.IsInfo) _logger.Info($"Pruning operation timed out at timestamp {cutoffTimestamp}. Deleted {deletedBlocks} blocks.");
                    break;
                }

                // should never happen
                if (number == 0 || number >= _blockTree.SyncPivot.BlockNumber)
                {
                    if (_logger.IsWarn) _logger.Warn($"Encountered unexepected block #{number} while pruning history, this block will not be deleted. Should be in range (0, {_blockTree.SyncPivot.BlockNumber}).");
                    continue;
                }

                long? remaining = CutoffBlockNumber;
                remaining = remaining is null ? null : long.Min(remaining!.Value, _blockTree.SyncPivot.BlockNumber) - _deletePointer;
                if (_logger.IsInfo && deletedBlocks % _deletionProgressLoggingInterval == 0)
                {
                    string suffix = remaining is null ? "could not calculate cutoff." : $"with {remaining} remaining.";
                    _logger.Info($"Historical block pruning in progress... Deleted {deletedBlocks} blocks, " + suffix);
                }

                if (_logger.IsDebug) _logger.Debug($"Deleting old block {number} with hash {hash}.");
                _blockTree.DeleteOldBlock(number, hash);
                _receiptStorage.RemoveReceipts(block);

                UpdateDeletePointer(number + 1, remaining is null || remaining == 0);
                lastDeletedTimstamp = block.Timestamp;
                deletedBlocks++;
                Metrics.BlocksPruned++;
            }
        }
        finally
        {
            if (_cutoffPointer < _deletePointer && lastDeletedTimstamp is not null)
            {
                _cutoffPointer = _deletePointer;
                _cutoffTimestamp = lastDeletedTimstamp;
                Metrics.PruningCutoffBlocknumber = _cutoffPointer;
                Metrics.PruningCutoffTimestamp = _cutoffTimestamp;
            }
            SaveDeletePointer();

            if (!cancellationToken.IsCancellationRequested)
            {
                if (_logger.IsInfo) _logger.Info($"Completed pruning operation up to timestamp {cutoffTimestamp}. Deleted {deletedBlocks} blocks up to #{_deletePointer}.");
                _lastPrunedTimestamp = cutoffTimestamp;
            }
        }
    }

    private IEnumerable<Block> GetBlocksBeforeAncientBarrier()
        => GetBlocksByNumber(_deletePointer, long.Min(_ancientBarrier, _blockTree.SyncPivot.BlockNumber) - 1, (_) => false);

    private IEnumerable<Block> GetBlocksBeforeTimestamp(ulong cutoffTimestamp)
        => GetBlocksByNumber(_deletePointer, _blockTree.SyncPivot.BlockNumber - 1, b => b.Timestamp >= cutoffTimestamp);

    private IEnumerable<Block> GetBlocksByNumber(long from, long to, Predicate<Block> endSearch)
    {
        for (long i = from; i <= to; i++)
        {
            ChainLevelInfo? chainLevelInfo = _chainLevelInfoRepository.LoadLevel(i);
            if (chainLevelInfo is null)
            {
                continue;
            }

            bool finished = false;
            foreach (BlockInfo blockInfo in chainLevelInfo.BlockInfos)
            {
                Block? block = _blockTree.FindBlock(blockInfo.BlockHash, BlockTreeLookupOptions.None, i);
                if (block is null)
                {
                    continue;
                }

                // search entire chain level before finishing
                if (endSearch(block))
                {
                    finished = true;
                }
                else
                {
                    yield return block;
                }
            }

            if (finished)
            {
                break;
            }
        }
    }

    private ulong? CalculateCutoffTimestamp()
        => _historyConfig.Pruning == PruningModes.Rolling && _blockTree.Head is not null ?
            _blockTree.Head!.Timestamp - (ulong)(_historyConfig.RetentionEpochs * _epochLength) :
            null;

    private bool TryLoadDeletePointer()
    {
        if (_hasLoadedDeletePointer)
        {
            return true;
        }

        byte[]? val = _metadataDb.Get(MetadataDbKeys.HistoryPruningDeletePointer);
        if (val is null)
        {
            if (SetDeletePointerToOldestBlock())
            {
                _hasLoadedDeletePointer = true;
            }
        }
        else
        {
            UpdateDeletePointer(val.AsRlpStream().DecodeLong());
            _lastSavedDeletePointer = _deletePointer;
            _hasLoadedDeletePointer = true;
        }

        if (_logger.IsDebug) _logger.Debug($"Discovered oldest block stored #{_deletePointer}.");
        return _hasLoadedDeletePointer;
    }

    private void SaveDeletePointer()
    {
        if (!_hasLoadedDeletePointer || _deletePointer == _lastSavedDeletePointer)
        {
            return;
        }

        _metadataDb.Set(MetadataDbKeys.HistoryPruningDeletePointer, Rlp.Encode(_deletePointer).Bytes);
        _lastSavedDeletePointer = _deletePointer;
        if (_logger.IsDebug) _logger.Debug($"Persisting oldest block known = #{_deletePointer} to disk.");
    }

    private void UpdateDeletePointer(long newDeletePointer, bool isFinalUpdate = true)
    {
        _deletePointer = newDeletePointer;
        Metrics.OldestStoredBlockNumber = _deletePointer;
        _blockTree.NewOldestBlock(_deletePointer);
        BlockHeader? oldest = _blockTree.FindBlock(_deletePointer)?.Header;
        if (oldest is not null)
        {
            _deletePointerHeader = oldest;
            NewOldestBlock?.Invoke(this, new OnNewOldestBlockArgs(oldest, isFinalUpdate));
        }
    }
}
