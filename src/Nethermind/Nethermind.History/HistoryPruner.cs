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
using Nethermind.Config;
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
    private const int DeleteBatchSize = 64;
    private const int MaxOptimisticSearchAttempts = 3;

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
    private long _lastSavedDeletePointer = 1;
    private long? _cutoffPointer;
    private ulong? _cutoffTimestamp;
    private readonly int LoggingInterval;
    private bool _hasLoadedDeletePointer = false;
    private ulong _calledCounter = 0;

    public class HistoryPrunerException(string message, Exception? innerException = null) : Exception(message, innerException);

    public HistoryPruner(
        IBlockTree blockTree,
        IReceiptStorage receiptStorage,
        ISpecProvider specProvider,
        IChainLevelInfoRepository chainLevelInfoRepository,
        IDbProvider dbProvider,
        IHistoryConfig historyConfig,
        IBlocksConfig blocksConfig,
        IProcessExitSource processExitSource,
        IBackgroundTaskScheduler backgroundTaskScheduler,
        ILogManager logManager)
    {
        _specProvider = specProvider;
        _logger = logManager.GetClassLogger();
        LoggingInterval = 1;
        _blockTree = blockTree;
        _receiptStorage = receiptStorage;
        _chainLevelInfoRepository = chainLevelInfoRepository;
        _metadataDb = dbProvider.MetadataDb;
        _processExitSource = processExitSource;
        _backgroundTaskScheduler = backgroundTaskScheduler;
        _historyConfig = historyConfig;
        _enabled = historyConfig.Enabled;
        _epochLength = (long)blocksConfig.SecondsPerSlot * 32; // must be changed if slot length changes
        _minHistoryRetentionEpochs = specProvider.GenesisSpec.MinHistoryRetentionEpochs;

        _logger.Info("constructed history pruner");
        CheckConfig();

        if (historyConfig.DropPreMerge)
        {
            Metrics.PruningCutoffBlocknumber = _specProvider.MergeBlockNumber?.BlockNumber;
            Metrics.PruningCutoffTimestamp = _specProvider.BeaconChainGenesisTimestamp;
        }
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
            if (_blockTree.Head is null ||
                _blockTree.SyncPivot.BlockNumber == 0 ||
                !LoadDeletePointer() ||
                !ShouldPruneHistory(out ulong? cutoffTimestamp))
            {
                if (_logger.IsInfo) _logger.Debug($"[prune] Skipping historical block pruning.");
                return Task.CompletedTask;
            }

            _calledCounter++;
            if (_calledCounter % (ulong)_historyConfig.RunEvery != 0)
            {
                if (_logger.IsInfo) _logger.Debug($"[prune] Skipping historical block pruning counter={_calledCounter}.");
                return Task.CompletedTask;
            }

            if (_logger.IsInfo)
            {
                long? cutoff = CutoffBlockNumber;
                cutoff = cutoff is null ? null : long.Min(cutoff!.Value, _blockTree.SyncPivot.BlockNumber);
                long? toDelete = cutoff - _deletePointer;
                _logger.Info($"Pruning historical blocks up to timestamp {cutoffTimestamp} (#{(cutoff is null ? "unknown" : cutoff)}). Estimated {(toDelete is null ? "unknown" : toDelete)} blocks will be deleted.");
                _logger.Info($"[prune] SyncPivot={_blockTree.SyncPivot.BlockNumber} DeletePointer={_deletePointer}");
            }

            PruneBlocksAndReceipts(cutoffTimestamp!.Value, cancellationToken);
            return Task.CompletedTask;
        }
    }

    public long? CutoffBlockNumber
    {
        get => FindCutoffBlockNumber();
    }

    public long? OldestBlockNumber
    {
        get => _deletePointer;
    }

    internal bool FindOldestBlock()
    {
        bool found = false;
        lock (_searchLock)
        {
            // lock prune lock since _deletePointer could be altered
            lock (_pruneLock)
            {
                _logger.Info($"[prune] Searching for oldest block in range 1-{_blockTree.SyncPivot.BlockNumber}.");
                long? oldestBlockNumber = BlockTree.BinarySearchBlockNumber(1L, _blockTree.SyncPivot.BlockNumber, BlockExists, BlockTree.BinarySearchDirection.Down);

                if (oldestBlockNumber is not null)
                {
                    _deletePointer = oldestBlockNumber.Value;
                    found = true;
                }

                _logger.Info($"[prune] Found oldest block on disk #{oldestBlockNumber ?? -1}");

                SaveDeletePointer();
            }
        }
        return found;
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

    private long? FindCutoffBlockNumber()
    {
        if (!_enabled)
        {
            return null;
        }

        if (_historyConfig.DropPreMerge && _historyConfig.RetentionEpochs is null)
        {
            return _specProvider.MergeBlockNumber?.BlockNumber;
        }

        ulong? cutoffTimestamp = CalculateCutoffTimestamp();

        if (cutoffTimestamp is null)
        {
            return null;
        }

        long? cutoffBlockNumber = null;
        long searchCutoff = _blockTree.Head is null ? _blockTree.SyncPivot.BlockNumber : _blockTree.Head.Number;
        lock (_searchLock)
        {
            // cutoff is unchanged, can reuse
            if (_cutoffTimestamp is not null && cutoffTimestamp == _cutoffTimestamp)
            {
                _logger.Info($"[prune] cuttoff pointer unchanged #{_cutoffPointer}");
                return _cutoffPointer;
            }

            // optimisticly search a few blocks from old pointer
            if (_cutoffPointer is not null)
            {
                int attempts = 0;
                _logger.Info($"[prune] starting optimistic cutoff search in range {_cutoffPointer.Value}-{searchCutoff}");
                _ = GetBlocksByNumber(_cutoffPointer.Value, searchCutoff, b =>
                {
                    if (attempts >= MaxOptimisticSearchAttempts)
                    {
                        return true;
                    }

                    _logger.Info($"[prune] optimistic linear scanning level {b} for cutoff block");

                    bool afterCutoff = b.Timestamp >= cutoffTimestamp;
                    if (afterCutoff)
                    {
                        cutoffBlockNumber = b.Number;
                    }
                    attempts++;
                    return afterCutoff;
                }).ToList();
            }
            else
            {
                _logger.Info($"[prune] skipping optimistic cutoff search");
            }

            if (cutoffBlockNumber is null)
            {
                _logger.Info($"[prune] optimistic cutoff search failed.");
            }

            _logger.Info($"[prune] searching for cutoff block number in range {_deletePointer}-{searchCutoff}");

            // if linear search fails fallback to binary search
            cutoffBlockNumber ??= BlockTree.BinarySearchBlockNumber(_deletePointer, searchCutoff, (n, _) =>
            {
                BlockInfo[]? blockInfos = _chainLevelInfoRepository.LoadLevel(n)?.BlockInfos;

                _logger.Info($"[prune] scanning level {n} for cutoff block");

                if (blockInfos is null || blockInfos.Length == 0)
                {
                    _logger.Info($"[prune] no block found at level {n}");
                    _logger.Info($"[prune] block infos at level {n} = {blockInfos?.Length}");
                    return false;
                }

                foreach (BlockInfo blockInfo in blockInfos)
                {
                    Block? b = _blockTree.FindBlock(blockInfo.BlockHash, BlockTreeLookupOptions.None, n);
                    _logger.Info($"[prune] scanning block #{n}, found? {b is not null}. hash={blockInfo.BlockHash}");
                    if (b is not null)
                    {
                        _logger.Info($"[prune] found block at level {n} with timestamp {b.Timestamp}");
                        _logger.Info($"[prune] continue?={b.Timestamp >= cutoffTimestamp} cutoffTimestamp={cutoffTimestamp}");
                        if (b.Timestamp >= cutoffTimestamp)
                        {
                            return true;
                        }
                    }
                }

                return false;
            }, BlockTree.BinarySearchDirection.Down);

            _logger.Info($"[prune] Found cutoff block #{cutoffBlockNumber}");

            _cutoffTimestamp = cutoffTimestamp;
            _cutoffPointer = cutoffBlockNumber ?? _cutoffPointer;
            Metrics.PruningCutoffTimestamp = cutoffTimestamp;
            Metrics.PruningCutoffBlocknumber = _cutoffPointer;
        }

        return cutoffBlockNumber;
    }

    private void CheckConfig()
    {
        if (_historyConfig.RetentionEpochs is not null &&
            _historyConfig.RetentionEpochs < _minHistoryRetentionEpochs)
        {
            _logger.Error($"[prune] HistoryRetentionEpochs must be at least {_minHistoryRetentionEpochs}.");
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
        if (_blockTree.SyncPivot.BlockNumber == 0)
        {
            _logger.Info("[prune] skipping pruning, no sync pivot");
            return;
        }
        try
        {
            IEnumerable<Block> blocks = GetBlocksBeforeTimestamp(cutoffTimestamp);
            foreach (Block block in blocks)
            {
                long number = block.Number;
                Hash256 hash = block.Hash!;

                if (_logger.IsInfo) _logger.Info($"[prune] Scanning block {block.Number} for deletion");

                if (cancellationToken.IsCancellationRequested)
                {
                    if (_logger.IsInfo) _logger.Info($"[prune] Pruning operation timed out at timestamp {cutoffTimestamp}. Deleted {deletedBlocks} blocks.");
                    break;
                }

                // should never happen
                if (number == 0 || number >= _blockTree.SyncPivot.BlockNumber)
                {
                    if (_logger.IsWarn) _logger.Warn($"[prune] Encountered unexepected block #{number} while pruning history, this block will not be deleted. Should be in range (0, {_blockTree.SyncPivot.BlockNumber}).");
                    continue;
                }

                if (deletedBlocks % DeleteBatchSize == 0)
                {
                    batch?.Dispose();
                    batch = _chainLevelInfoRepository.StartBatch();
                    if (_logger.IsInfo) _logger.Info($"[prune] Starting new deletion batch");
                }

                if (_logger.IsInfo && deletedBlocks % LoggingInterval == 0)
                {
                    long? cutoff = CutoffBlockNumber;
                    cutoff = cutoff is null ? null : long.Min(cutoff!.Value, _blockTree.SyncPivot.BlockNumber) - _deletePointer;
                    string suffix = cutoff is null ? "could not calculate cutoff." : $"with {cutoff} remaining.";
                    _logger.Info($"[prune] Historical block pruning in progress... Deleted {deletedBlocks} blocks, " + suffix);
                }

                if (_logger.IsInfo) _logger.Info($"[prune] Deleting old block {number} with hash {hash}.");
                _blockTree.DeleteOldBlock(number, hash, batch!);
                _receiptStorage.RemoveReceipts(block);
                _deletePointer = number + 1;
                lastDeletedTimstamp = block.Timestamp;

                deletedBlocks++;
                Metrics.BlocksPruned++;
            }
        }
        finally
        {
            batch?.Dispose();

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
                if (_logger.IsInfo) _logger.Info($"[prune] Completed pruning operation up to timestamp {cutoffTimestamp}. Deleted {deletedBlocks} blocks up to #{_deletePointer}.");
                _lastPrunedTimestamp = cutoffTimestamp;
            }
            else
            {
                if (_logger.IsInfo) _logger.Info($"[prune] Pruning operation terminated early");
            }
        }
    }

    private IEnumerable<Block> GetBlocksBeforeTimestamp(ulong cutoffTimestamp)
        => GetBlocksByNumber(_deletePointer, _blockTree.SyncPivot.BlockNumber - 1, b => b.Timestamp >= cutoffTimestamp);

    private IEnumerable<Block> GetBlocksByNumber(long from, long to, Predicate<Block> endSearch)
    {
        _logger.Info($"[prune] Searching for blocks in range {from}-{to}.");
        for (long i = from; i < to; i++)
        {
            ChainLevelInfo? chainLevelInfo = _chainLevelInfoRepository.LoadLevel(i);
            if (chainLevelInfo is null || chainLevelInfo.BlockInfos.Length == 0)
            {
                _logger.Info($"[prune] Skipping empty level {i}.");
                continue;
            }

            // if (chainLevelInfo.MainChainBlock is null)
            // {
            //     _logger.Info($"[prune] No main chain block on level {i}.");
            // }
            // else
            // {
            //     _logger.Info($"[prune] Main chain block on level {i}: {chainLevelInfo.MainChainBlock.BlockNumber}.");
            // }

            bool finished = false;
            foreach (BlockInfo blockInfo in chainLevelInfo.BlockInfos)
            {
                Block? block = _blockTree.FindBlock(blockInfo.BlockHash, BlockTreeLookupOptions.None, i);
                _logger.Info($"[prune] scanning blockinfo #{i} found?={block is not null} hash={blockInfo.BlockHash}");
                if (block is null)
                {
                    _logger.Info($"[prune] Skipping block which wasn't found {i}.");
                    continue;
                }

                _logger.Info($"[prune] found block #{block.Number} timestamp={block.Timestamp}, hash={block.Hash}.");

                // search entire chain level before finishing
                if (endSearch(block))
                {
                    _logger.Info($"[prune] Ending search on level {i}, block hash {blockInfo.BlockHash}.");
                    _logger.Info($"[prune] timestamp={block.Timestamp}.");
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

        if (_historyConfig.RetentionEpochs.HasValue && _blockTree.Head is not null)
        {
            cutoffTimestamp = _blockTree.Head!.Timestamp - (ulong)(_historyConfig.RetentionEpochs.Value * _epochLength);
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

    private bool LoadDeletePointer()
    {
        if (_logger.IsInfo) _logger.Info($"[prune] Starting search for oldest block stored.");
        byte[]? val = _metadataDb.Get(MetadataDbKeys.HistoryPruningDeletePointer);
        if (val is null)
        {
            if (FindOldestBlock())
            {
                _hasLoadedDeletePointer = true;
            }
        }
        else
        {
            _deletePointer = val.AsRlpStream().DecodeLong();
            Metrics.OldestStoredBlockNumber = _deletePointer;
            _hasLoadedDeletePointer = true;
        }

        if (_logger.IsDebug) _logger.Debug($"[prune] Discovered oldest block stored #{_deletePointer}.");
        return _hasLoadedDeletePointer;
    }

    private void SaveDeletePointer()
    {
        if (!_hasLoadedDeletePointer || _deletePointer == _lastSavedDeletePointer)
        {
            if (_logger.IsInfo) _logger.Info($"[prune] NOT Persisting oldest block known = #{_deletePointer} to disk.");
            return;
        }

        _metadataDb.Set(MetadataDbKeys.HistoryPruningDeletePointer, Rlp.Encode(_deletePointer).Bytes);
        _lastSavedDeletePointer = _deletePointer;
        Metrics.OldestStoredBlockNumber = _deletePointer;
        if (_logger.IsInfo) _logger.Info($"[prune] Persisting oldest block known = #{_deletePointer} to disk.");
    }
}
