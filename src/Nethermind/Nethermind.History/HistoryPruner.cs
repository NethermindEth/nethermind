// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Config;
using Nethermind.Consensus.Scheduler;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Repositories;

namespace Nethermind.History;

public class HistoryPruner : IHistoryPruner
{
    // only one pruning and one searching thread at a time
    private readonly Lock _pruneLock = new();
    private readonly Lock _searchLock = new();
    private ulong _lastPrunedTimestamp;
    private readonly ISpecProvider _specProvider;
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
    private readonly long _minHistoryRetentionEpochs;
    private long _deletePointer = 1;
    private long? _cutoffPointer;
    private ulong? _cutoffTimestamp;
    private const int DeleteBatchSize = 64;

    public class HistoryPrunerException(string message, Exception? innerException = null) : Exception(message, innerException);

    public HistoryPruner(
        IBlockTree blockTree,
        IReceiptStorage receiptStorage,
        ISpecProvider specProvider,
        IChainLevelInfoRepository chainLevelInfoRepository,
        IDb metadataDb,
        IHistoryConfig historyConfig,
        long secondsPerSlot,
        IProcessExitSource processExitSource,
        IBackgroundTaskScheduler backgroundTaskScheduler,
        ILogManager logManager)
    {
        _specProvider = specProvider;
        _logger = logManager.GetClassLogger();
        _blockTree = blockTree;
        _receiptStorage = receiptStorage;
        _chainLevelInfoRepository = chainLevelInfoRepository;
        _metadataDb = metadataDb;
        _processExitSource = processExitSource;
        _backgroundTaskScheduler = backgroundTaskScheduler;
        _historyConfig = historyConfig;
        _enabled = historyConfig.Enabled;
        _epochLength = secondsPerSlot * 32;
        _minHistoryRetentionEpochs = specProvider.GenesisSpec.MinHistoryRetentionEpochs;

        CheckConfig();
        LoadDeletePointer();
    }

    public void OnBlockProcessorQueueEmpty(object? sender, EventArgs e)
        => SchedulePruneHistory(_processExitSource.Token);

    public void SchedulePruneHistory(CancellationToken cancellationToken)
        => _backgroundTaskScheduler.ScheduleTask(1,
            (_, backgroundTaskToken) =>
            {
                var cts = CancellationTokenSource.CreateLinkedTokenSource(backgroundTaskToken, cancellationToken);
                return TryPruneHistory(cts.Token);
            });

    public Task TryPruneHistory(CancellationToken cancellationToken)
    {
        lock (_pruneLock)
        {
            if (_blockTree.Head is null || !ShouldPruneHistory(out ulong? cutoffTimestamp))
            {
                if (_logger.IsDebug) _logger.Debug($"Skipping historical block pruning.");
                return Task.CompletedTask;
            }

            if (_logger.IsInfo) _logger.Info($"Pruning historical blocks up to timestamp {cutoffTimestamp} (#{CutoffBlockNumber})");

            PruneBlocksAndReceipts(cutoffTimestamp!.Value, cancellationToken);
            return Task.CompletedTask;
        }
    }

    public long? CutoffBlockNumber
    {
        get => FindCutoffBlockNumber();
    }

    private void FindOldestBlock()
    {
        lock (_searchLock)
        {
            // lock prune lock since _deletePointer could be altered
            lock (_pruneLock)
            {
                long? oldestBlockNumber = BlockTree.BinarySearchBlockNumber(1L, _blockTree.SyncPivot.BlockNumber, LevelExists);

                if (oldestBlockNumber is not null)
                {
                    _deletePointer = oldestBlockNumber.Value;
                }

                SaveDeletePointer();
            }
        }
    }

    private bool LevelExists(long n, bool _)
        => _chainLevelInfoRepository.LoadLevel(n) is not null;

    private long? FindCutoffBlockNumber()
    {
        if (!_enabled)
        {
            return null;
        }

        if (_historyConfig.DropPreMerge && _historyConfig.HistoryRetentionEpochs is null)
        {
            return _specProvider.MergeBlockNumber?.BlockNumber;
        }

        ulong? cutoffTimestamp = CalculateCutoffTimestamp();

        if (cutoffTimestamp is null)
        {
            return null;
        }

        long? cutoffBlockNumber = null;
        lock (_searchLock)
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
                GetBlocksByNumber(_cutoffPointer.Value, b =>
                {
                    if (attempts >= 5)
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
                }, _ => { });
            }

            // if linear search fails fallback to  binary search
            cutoffBlockNumber ??= BlockTree.BinarySearchBlockNumber(_deletePointer, _blockTree.SyncPivot.BlockNumber, (n, _) =>
            {
                BlockInfo? blockInfo = _chainLevelInfoRepository.LoadLevel(n)?.MainChainBlock;
                Block? block = blockInfo is null ? null : _blockTree.FindBlock(blockInfo.BlockHash, BlockTreeLookupOptions.None, blockInfo.BlockNumber);

                // find cutoff point
                return block is not null && block.Timestamp >= cutoffTimestamp;
            });

            _cutoffTimestamp = cutoffTimestamp;
            _cutoffPointer = cutoffBlockNumber ?? _cutoffPointer;
        }

        return cutoffBlockNumber;
    }

    private void CheckConfig()
    {
        if (_historyConfig.HistoryRetentionEpochs is not null &&
            _historyConfig.HistoryRetentionEpochs < _minHistoryRetentionEpochs)
        {
            _logger.Error($"HistoryRetentionEpochs must be at least {_minHistoryRetentionEpochs}.");
            // throw new HistoryPrunerException($"HistoryRetentionEpochs must be at least {_minHistoryRetentionEpochs}.");
        }
    }

    private bool ShouldPruneHistory(out ulong? cutoffTimestamp)
    {
        if (!_enabled)
        {
            cutoffTimestamp = null;
            return false;
        }

        cutoffTimestamp = CalculateCutoffTimestamp();
        return cutoffTimestamp is not null && cutoffTimestamp > _lastPrunedTimestamp;
    }

    private void PruneBlocksAndReceipts(ulong cutoffTimestamp, CancellationToken cancellationToken)
    {
        int deletedBlocks = 0;
        ulong? lastDeletedTimstamp = null;
        BatchWrite? batch = null;
        try
        {
            IEnumerable<Block> blocks = GetBlocksBeforeTimestamp(cutoffTimestamp);
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

                if (deletedBlocks % DeleteBatchSize == 0)
                {
                    batch?.Dispose();
                    batch = _chainLevelInfoRepository.StartBatch();
                }

                if (_logger.IsDebug) _logger.Debug($"Deleting old block {number} with hash {hash}.");
                _blockTree.DeleteBlock(number, hash, null, batch!, null, true);
                _receiptStorage.RemoveReceipts(block);
                _deletePointer = number;
                lastDeletedTimstamp = block.Timestamp;
                deletedBlocks++;
            }
        }
        finally
        {
            batch?.Dispose();

            if (_cutoffPointer < _deletePointer && lastDeletedTimstamp is not null)
            {
                _cutoffPointer = _deletePointer;
                _cutoffTimestamp = lastDeletedTimstamp;
            }
            SaveDeletePointer();

            if (!cancellationToken.IsCancellationRequested)
            {
                if (_logger.IsInfo) _logger.Info($"Completed pruning operation up to timestamp {cutoffTimestamp}. Deleted {deletedBlocks} blocks up to #{_deletePointer}.");
                _lastPrunedTimestamp = cutoffTimestamp;
            }
        }
    }

    private IEnumerable<Block> GetBlocksBeforeTimestamp(ulong cutoffTimestamp)
        => GetBlocksByNumber(_deletePointer, b => b.Timestamp >= cutoffTimestamp, i => _deletePointer = i);

    private IEnumerable<Block> GetBlocksByNumber(long from, Predicate<Block> endSearch, Action<long> onFirstBlock)
    {
        bool firstBlock = true;
        for (long i = from; i <= _blockTree.SyncPivot.BlockNumber; i++)
        {
            ChainLevelInfo? chainLevelInfo = _chainLevelInfoRepository.LoadLevel(i);
            if (chainLevelInfo is null)
            {
                continue;
            }
            else if (firstBlock)
            {
                onFirstBlock(i);
                firstBlock = false;
            }

            bool finished = false;
            foreach (BlockInfo blockInfo in chainLevelInfo.BlockInfos)
            {
                Block? block = _blockTree.FindBlock(blockInfo.BlockHash, BlockTreeLookupOptions.None, blockInfo.BlockNumber);
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
    {
        ulong? cutoffTimestamp = null;

        if (_historyConfig.HistoryRetentionEpochs.HasValue && _blockTree.Head is not null)
        {
            cutoffTimestamp = _blockTree.Head!.Timestamp - (ulong)(_historyConfig.HistoryRetentionEpochs.Value * _epochLength);
        }

        if (_historyConfig.DropPreMerge)
        {
            ulong? beaconGenesisTimestamp = _specProvider.BeaconChainGenesisTimestamp;
            if (beaconGenesisTimestamp.HasValue && (cutoffTimestamp is null || beaconGenesisTimestamp.Value > cutoffTimestamp))
            {
                cutoffTimestamp = beaconGenesisTimestamp.Value;
            }
        }

        return cutoffTimestamp;
    }

    private void LoadDeletePointer()
    {
        byte[]? val = _metadataDb.Get(MetadataDbKeys.HistoryPruningDeletePointer);
        if (val is null)
        {
            FindOldestBlock();
        }
        else
        {
            _deletePointer = val.AsRlpStream().DecodeLong();
            Metrics.OldestStoredBlockNumber = _deletePointer;
        }

        if (_logger.IsDebug) _logger.Debug($"Discovered oldest block stored #{_deletePointer}.");
    }

    private void SaveDeletePointer()
    {
        _metadataDb.Set(MetadataDbKeys.HistoryPruningDeletePointer, Rlp.Encode(_deletePointer).Bytes);
        Metrics.OldestStoredBlockNumber = _deletePointer;
        if (_logger.IsDebug) _logger.Debug($"Persisting oldest block known = #{_deletePointer} to disk.");
    }
}
